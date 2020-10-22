﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    public class MemoryMappedFileAccessorWindows : MemoryMappedFileAccessor
    {
        public MemoryMappedFileAccessorWindows(ILogger logger) : base(logger)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new Exception($"Cannot instantiate on this platform");
            }
        }

        public MemoryMappedFileAccessorWindows(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new Exception($"Cannot instantiate on this platform");
            }
        }

        public override bool TryCreate(string mapName, long size, out MemoryMappedFile mmf)
        {
            mmf = null;

            try
            {
                mmf = MemoryMappedFile.CreateNew(
                    mapName, // Named maps are supported on Windows
                    size,
                    MemoryMappedFileAccess.ReadWrite);
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Cannot create MemoryMappedFile {mapName} for {size} bytes");
            }

            return false;
        }

        public override bool TryOpen(string mapName, out MemoryMappedFile mmf)
        {
            mmf = null;

            try
            {
                mmf = MemoryMappedFile.OpenExisting(
                    mapName,
                    MemoryMappedFileRights.ReadWrite,
                    HandleInheritability.Inheritable);
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Cannot open file: {mapName}");
                return false;
            }
        }

        public override void Delete(string mapName, MemoryMappedFile mmf)
        {
            if (mmf != null)
            {
                mmf.Dispose();
            }
        }
    }
}