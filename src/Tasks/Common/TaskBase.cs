﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks
{
    public abstract class TaskBase : Task
    {
        //static bool s_debuggingAttempted;


        // no reason for outside classes to derive from this class.
        internal TaskBase()
        {
//#if DEBUG
//            try
//            {
//                if (!Debugger.IsAttached
//                    && !s_debuggingAttempted
//                    && string.Equals(
//                        Environment.GetEnvironmentVariable("DEBUG_MICROSOFT_NET_BUILD_TASKS"),
//                        "true",
//                        StringComparison.OrdinalIgnoreCase))
//                {
//                    s_debuggingAttempted = true;
//                    Debug();
//                }
//            }
//            catch (DllNotFoundException)
//            {
//            }
//#endif
        }

        private static bool Debug(Exception ex)
        {
            if (!Debugger.IsAttached)
            {
                MessageBoxW(
                    IntPtr.Zero,
                    $"Attach to Process {Process.GetCurrentProcess().Id} to debug build task",
                    "Debug Build Task");
            }

            Debugger.Break();

            return false;
        }

        public override bool Execute()
        {

            try
            {
                ExecuteCore();
            }
            catch (BuildErrorException e)
            {
                Log.LogErrorFromException(e);
            }
#if DEBUG
            catch (Exception e) when (Debug(e))
            {
                // Never hit: filter returns false after giving opportunity to break in debugger.
            }
#endif

            return !Log.HasLoggedErrors;
        }

        protected abstract void ExecuteCore();

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int MessageBoxW(
            IntPtr hwnd,
            string text,
            string caption,
            uint type = 0);
    }
}
