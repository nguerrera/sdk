// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.ProjectModel;

namespace Microsoft.NET.Build.Tasks
{
    public sealed class ResolvePackageAssets : TaskBase
    {
        [Required]
        public string ProjectAssetsCacheFile { get; set; }

        [Required]
        public string ProjectAssetsFile { get; set; }

        [Required]
        public string ProjectPath { get; set; }

        [Required]
        public string TargetFrameworkMoniker { get; set; }

        public bool DisableTransitiveProjectReferences { get; set; }
        public bool DisableFrameworkAssemblies { get; set; }
        public bool MarkPackageReferencesAsExternallyResolved { get; set; }
        public string ProjectLanguage { get; set; }
        public string RuntimeIdentifier { get; set; }

        /// <summary>
        /// Check that there is at least one package dependency in the RID graph that is not in the RID-agnostic graph.
        /// Used as a heuristic to detect invalid RIDs.
        /// </summary>
        public bool EnsureRuntimePackageDependencies { get; set; }

        [Output]
        public ITaskItem[] Analyzers { get; private set; }

        [Output]
        public ITaskItem[] CompileTimeAssemblies { get; private set; }

        [Output]
        public ITaskItem[] ContentFilesToPreprocess { get; private set; }

        [Output]
        public ITaskItem[] FrameworkAssemblies { get; private set; }

        [Output]
        public ITaskItem[] NativeLibraries { get; private set; }

        [Output]
        public ITaskItem[] ResourceAssemblies { get; private set; }

        [Output]
        public ITaskItem[] RuntimeAssemblies { get; private set; }

        [Output]
        public ITaskItem[] RuntimeTargets { get; private set; }

        [Output]
        public ITaskItem[] TransitiveProjectReferences { get; private set; }

        private const int CacheFormatSignature = ('P' << 0) | ('K' << 8) | ('G' << 16) | ('A' << 24); // "package assets"
        private const int CacheFormatVersion = 1;
        private const int ItemGroupMarker = -1;
        private const int ItemMarker = -2;
        private static readonly Encoding s_textEncoding = Encoding.UTF8;
        private const int SettingsHashLength = 256 / 8;
        private HashAlgorithm CreateSettingsHash() => SHA256.Create();

        protected override void ExecuteCore()
        {
            using (var reader = new CacheReader(this))
            {
                Analyzers = reader.ReadItemGroup();
                CompileTimeAssemblies = reader.ReadItemGroup();
                ContentFilesToPreprocess = reader.ReadItemGroup();
                FrameworkAssemblies = reader.ReadItemGroup();
                NativeLibraries = reader.ReadItemGroup();
                ResourceAssemblies = reader.ReadItemGroup();
                RuntimeAssemblies = reader.ReadItemGroup();
                RuntimeTargets = reader.ReadItemGroup();
                TransitiveProjectReferences = reader.ReadItemGroup();
            }

            string externallyResolved = MarkPackageReferencesAsExternallyResolved ? "true" : "";
            foreach (var item in CompileTimeAssemblies)
            {
                item.SetMetadata(MetadataKeys.NuGetSourceType, "Package");
                item.SetMetadata(MetadataKeys.NuGetIsFrameworkReference, "true");
                item.SetMetadata(MetadataKeys.Private, "false");
                item.SetMetadata(MetadataKeys.HintPath, item.ItemSpec);
                item.SetMetadata(MetadataKeys.ExternallyResolved, externallyResolved);
            }

            foreach (var item in FrameworkAssemblies)
            {
                item.SetMetadata(MetadataKeys.NuGetSourceType, "Package");
                item.SetMetadata(MetadataKeys.NuGetIsFrameworkReference, "true");
                item.SetMetadata(MetadataKeys.Pack, "false");
                item.SetMetadata(MetadataKeys.Private, "false");
            }
        }

        private byte[] HashSettings()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, s_textEncoding, leaveOpen: true))
                {
                    writer.Write(ProjectPath);
                    writer.Write(TargetFrameworkMoniker);
                    writer.Write(DisableTransitiveProjectReferences);
                    writer.Write(DisableFrameworkAssemblies);
                    writer.Write(MarkPackageReferencesAsExternallyResolved);
                    writer.Write(ProjectLanguage ?? "");
                    writer.Write(RuntimeIdentifier ?? "");
                }

                stream.Position = 0;
                using (var hash = CreateSettingsHash())
                {
                    return hash.ComputeHash(stream);
                }
            }
        }

        private sealed class CacheReader : IDisposable
        {
            private BinaryReader _reader;
            private string[] _stringTable;

            public CacheReader(ResolvePackageAssets task)
            {
                byte[] settingsHash = task.HashSettings();
                BinaryReader reader = null;

                try
                {
                    if (File.GetLastWriteTimeUtc(task.ProjectAssetsCacheFile) >= File.GetLastWriteTimeUtc(task.ProjectAssetsFile))
                    {
                        reader = OpenCacheFile(task.ProjectAssetsCacheFile, settingsHash);
                    }
                }
                catch (IOException)
                {
                }

                if (reader == null)
                {
                    using (var writer = new CacheWriter(task))
                    {
                        writer.Write();
                    }

                    reader = OpenCacheFile(task.ProjectAssetsCacheFile, settingsHash);
                }

                _reader = reader;
                ReadStringTable();
            }

            private static BinaryReader OpenCacheFile(string path, byte[] settingsHash)
            {
                var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var reader = new BinaryReader(stream, s_textEncoding, leaveOpen: false);

                try
                {
                    ValidateHeader(reader, settingsHash);
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }

                return reader;
            }

            private static void ValidateHeader(BinaryReader reader, byte[] settingsHash)
            {
                if (reader.ReadInt32() != CacheFormatSignature
                    || reader.ReadInt32() != CacheFormatVersion
                    || !reader.ReadBytes(SettingsHashLength).SequenceEqual(settingsHash))
                {
                    throw new InvalidDataException();
                }
            }

            private void ReadStringTable()
            {
                int stringTablePosition = _reader.ReadInt32();
                int savedPosition = Position;
                Position = stringTablePosition;

                _stringTable = new string[_reader.ReadInt32()];
                for (int i = 0; i < _stringTable.Length; i++)
                {
                    _stringTable[i] = _reader.ReadString();
                }

                Position = savedPosition;
            }

            private int Position
            {
                get => checked((int)_reader.BaseStream.Position);
                set => _reader.BaseStream.Position = value;
            }

            public void Dispose()
            {
                _reader.Dispose();
            }

            internal ITaskItem[] ReadItemGroup()
            {
                var items = new ITaskItem[_reader.ReadInt32()];

                for (int i = 0; i < items.Length; i++)
                {
                    items[i] = ReadItem();
                }

                return items;
            }

            private ITaskItem ReadItem()
            {
                var item = new TaskItem(_reader.ReadString());
                int metadataCount = _reader.ReadInt32();

                for (int i = 0; i < metadataCount; i++)
                {
                    string key = _stringTable[_reader.ReadInt32()];
                    string value = _stringTable[_reader.ReadInt32()];
                    item.SetMetadata(key, value);
                }

                return item;
            }
        }

        private sealed class CacheWriter : IDisposable
        {
            private const int InitialStringTableCapacity = 100;

            private ResolvePackageAssets _task;
            private BinaryWriter _writer;
            private LockFile _lockFile;
            private NuGetPackageResolver _packageResolver;
            private LockFileTarget _compileTimeTarget;
            private LockFileTarget _runtimeTarget;
            private Dictionary<string, int> _stringTable;
            private List<string> _metadataStrings;
            private List<int> _bufferedMetadata;
            private Placeholder _metadataStringTablePosition;
            private int _itemCount;

            public CacheWriter(ResolvePackageAssets task)
            {
                var targetFramework = NuGetUtils.ParseFrameworkName(task.TargetFrameworkMoniker);

                Directory.CreateDirectory(Path.GetDirectoryName(task.ProjectAssetsCacheFile));
                var stream = File.Open(task.ProjectAssetsCacheFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                _writer = new BinaryWriter(stream, s_textEncoding, leaveOpen: false);
                

                _task = task;
                _lockFile = new LockFileCache(task.BuildEngine4).GetLockFile(task.ProjectAssetsFile);
                _packageResolver = NuGetPackageResolver.CreateResolver(_lockFile, _task.ProjectPath);
                _compileTimeTarget = _lockFile.GetTargetAndThrowIfNotFound(targetFramework, runtime: null);
                _runtimeTarget = _lockFile.GetTargetAndThrowIfNotFound(targetFramework, _task.RuntimeIdentifier);
                _stringTable = new Dictionary<string, int>(InitialStringTableCapacity, StringComparer.Ordinal);
                _metadataStrings = new List<string>(InitialStringTableCapacity);
                _bufferedMetadata = new List<int>();
            }

            public void Dispose()
            {
                _writer.Dispose();
            }

            private void FlushMetadata()
            {
                if (_itemCount == 0)
                {
                    return;
                }

                Debug.Assert((_bufferedMetadata.Count % 2) == 0);

                _writer.Write(_bufferedMetadata.Count / 2);

                foreach (int m in _bufferedMetadata)
                {
                    _writer.Write(m);
                }

                _bufferedMetadata.Clear();
            }

            public void Write()
            {
                WriteHeader();
                WriteItemGroups();
                WriteMetadataStringTable();
            }

            private void WriteHeader()
            {
                _writer.Write(CacheFormatSignature);
                _writer.Write(CacheFormatVersion);

                byte[] hash = _task.HashSettings();
                _writer.Write(_task.HashSettings());
                _metadataStringTablePosition = WritePlaceholder();
            }

            private void WriteItemGroups()
            {
                WriteItemGroup(WriteAnalyzers);
                WriteItemGroup(WriteCompileTimeAssemblies);
                WriteItemGroup(WriteContentFilesToPreprocess);
                WriteItemGroup(WriteFrameworkAssemblies);
                WriteItemGroup(WriteNativeLibraries);
                WriteItemGroup(WriteResourceAssemblies);
                WriteItemGroup(WriteRuntimeAssemblies);
                WriteItemGroup(WriteRuntimeTargets);
                WriteItemGroup(WriteTransitiveProjectReferences);
            }

            private void WriteMetadataStringTable()
            {
                int savedPosition = Position;

                _writer.Write(_metadataStrings.Count);

                foreach (var s in _metadataStrings)
                {
                    _writer.Write(s);
                }

                WriteToPlaceholder(_metadataStringTablePosition, savedPosition);
            }

            private int Position
            {
                get => checked((int)_writer.BaseStream.Position);
                set => _writer.BaseStream.Position = value;
            }

            private struct Placeholder
            {
                public readonly int Position;
                public Placeholder(int position) { Position = position; }
            }

            private Placeholder WritePlaceholder()
            {
                var placeholder = new Placeholder(Position);
                _writer.Write(int.MinValue);
                return placeholder;
            }

            private void WriteToPlaceholder(Placeholder placeholder, int value)
            {
                int savedPosition = Position;
                Position = placeholder.Position;
                _writer.Write(value);
                Position = savedPosition;
            }

            private void WriteAnalyzers()
            {
                Dictionary<string, LockFileTargetLibrary> targetLibraries = null;

                foreach (var library in _lockFile.Libraries)
                {
                    if (!library.IsPackage())
                    {
                        continue;
                    }

                    foreach (var file in library.Files)
                    {
                        if (!NuGetUtils.IsApplicableAnalyzer(file, _task.ProjectLanguage))
                        {
                            continue;
                        }

                        if (targetLibraries == null)
                        {
                            targetLibraries = _runtimeTarget
                                .Libraries
                                .ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
                        }

                        if (targetLibraries.TryGetValue(library.Name, out var targetLibrary))
                        {
                            WriteItem(ResolvePackageAssetPath(targetLibrary, file), targetLibrary);
                        }
                    }
                }
            }

            private void WriteItemGroup(Action writeItems)
            {
                var placeholder = WritePlaceholder();
                _itemCount = 0;
                writeItems();
                FlushMetadata();
                WriteToPlaceholder(placeholder, _itemCount);
            }

            private void WriteCompileTimeAssemblies()
            {
                WriteItems(
                    _compileTimeTarget,
                    package => package.CompileTimeAssemblies);
            }

            private void WriteContentFilesToPreprocess()
            {

                WriteItems(
                    _runtimeTarget,
                    p => p.ContentFiles,
                    filter: asset => !string.IsNullOrEmpty(asset.PPOutputPath),
                    writeMetadata: asset =>
                    {
                        WriteMetadata(MetadataKeys.BuildAction, asset.BuildAction.ToString());
                        WriteMetadata(MetadataKeys.CopyToOutput, asset.CopyToOutput.ToString());
                        WriteMetadata(MetadataKeys.PPOutputPath, asset.PPOutputPath);
                        WriteMetadata(MetadataKeys.OutputPath, asset.OutputPath);
                        WriteMetadata(MetadataKeys.CodeLanguage, asset.CodeLanguage);
                    });
            }

            private void WriteFrameworkAssemblies()
            {
                if (_task.DisableFrameworkAssemblies)
                {
                    return;
                }

                foreach (var library in _compileTimeTarget.Libraries)
                {
                    if (!library.IsPackage())
                    {
                        continue;
                    }

                    foreach (string frameworkAssembly in library.FrameworkAssemblies)
                    {
                        WriteItem(frameworkAssembly, library);
                    }
                }
            }

            private void WriteNativeLibraries()
            {
                WriteItems(
                    _runtimeTarget,
                    package => package.NativeLibraries);
            }

            private void WriteResourceAssemblies()
            {
                WriteItems(
                    _runtimeTarget,
                    package => package.ResourceAssemblies,
                    writeMetadata: asset =>
                    {
                        string locale = asset.Properties["locale"];
                        WriteMetadata(MetadataKeys.Culture, locale);
                        WriteMetadata(MetadataKeys.DestinationSubDirectory, locale + Path.DirectorySeparatorChar);
                    });
            }

            private void WriteRuntimeAssemblies()
            {
                WriteItems(
                    _runtimeTarget,
                    package => package.RuntimeAssemblies);
            }

            private void WriteRuntimeTargets()
            {
                WriteItems(
                    _runtimeTarget,
                    package => package.RuntimeTargets,
                    writeMetadata: asset =>
                    {
                        string directory = Path.GetDirectoryName(asset.Path);
                        WriteMetadata(MetadataKeys.DestinationSubDirectory, directory + Path.DirectorySeparatorChar);
                    });
            }

            private void WriteTransitiveProjectReferences()
            {
                if (_task.DisableTransitiveProjectReferences)
                {
                    return;
                }

                Dictionary<string, string> projectReferencePaths = null;
                HashSet<string> directProjectDependencies = null;

                foreach (var library in _runtimeTarget.Libraries)
                {
                    if (!library.IsTransitiveProjectReference(_lockFile, ref directProjectDependencies))
                    {
                        continue;
                    }

                    if (projectReferencePaths == null)
                    {
                        projectReferencePaths = GetProjectReferencePaths(_lockFile);
                    }

                    if (!directProjectDependencies.Contains(library.Name))
                    {
                        WriteItem(projectReferencePaths[library.Name], library);
                    }
                }
            }

            private void WriteItems<T>(
                LockFileTarget target,
                Func<LockFileTargetLibrary, IList<T>> getAssets,
                Func<T, bool> filter = null,
                Action<T> writeMetadata = null)
                where T : LockFileItem
            {
                foreach (var library in target.Libraries)
                {
                    if (!library.IsPackage())
                    {
                        continue;
                    }

                    foreach (T asset in getAssets(library))
                    {
                        if (asset.IsPlaceholderFile() || (filter != null && !filter.Invoke(asset)))
                        {
                            continue;
                        }

                        string itemSpec = ResolvePackageAssetPath(library, asset.Path);
                        WriteItem(itemSpec, library);
                        writeMetadata?.Invoke(asset);
                    }
                }
            }

            private void WriteItem(string itemSpec, LockFileTargetLibrary package)
            {
                FlushMetadata();
                _itemCount++;
                _writer.Write(itemSpec);
                WriteMetadata(MetadataKeys.NuGetPackageId, package.Name);
                WriteMetadata(MetadataKeys.NuGetPackageVersion, package.Version.ToNormalizedString());
            }

            private void WriteMetadata(string key, string value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _bufferedMetadata.Add(GetMetadataIndex(key));
                    _bufferedMetadata.Add(GetMetadataIndex(value));
                }
            }

            private int GetMetadataIndex(string value)
            {
                if (!_stringTable.TryGetValue(value, out int index))
                {
                    index = _metadataStrings.Count;
                    _stringTable.Add(value, index);
                    _metadataStrings.Add(value);
                }

                return index;
            }

            private string ResolvePackageAssetPath(LockFileTargetLibrary package, string relativePath)
            {
                string packagePath = _packageResolver.GetPackageDirectory(package.Name, package.Version);
                return Path.Combine(packagePath, NormalizeRelativePath(relativePath));
            }

            private static Dictionary<string, string> GetProjectReferencePaths(LockFile lockFile)
            {
                Dictionary<string, string> paths = new Dictionary<string, string>();

                foreach (var library in lockFile.Libraries)
                {
                    if (library.IsProject())
                    {
                        paths[library.Name] = NormalizeRelativePath(library.MSBuildProject);
                    }
                }

                return paths;
            }

            private static string NormalizeRelativePath(string relativePath)
                => relativePath.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
