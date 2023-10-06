﻿/*
The MIT License(MIT)

Copyright(c) 2015 IgorSoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Threading.Tasks;

using DokanNet;

using NLog;

using IgorSoft.CloudFS.Interfaces.IO;
using IgorSoft.DokanCloudFS.IO;

using FileAccess = DokanNet.FileAccess;

namespace IgorSoft.DokanCloudFS
{
    [System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
    public partial class CloudOperations : IDokanOperations
    {
        [System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
        private class StreamContext : IDisposable
        {
            public CloudFileNode File { get; }

            public FileAccess OriginalAccess { get; }

            public FileAccess Access { get; }

            public Stream Stream { get; set; }

            public Task Task { get; set; }

            public bool IsLocked { get; set; }

            public bool CanWriteDelayed => Access.HasFlag(FileAccess.WriteData) && (Stream?.CanRead ?? false) && Task == null;

            public StreamContext(CloudFileNode file, FileAccess access, FileAccess originalAccess)
            {
                File = file;
                Access = access;
                OriginalAccess = originalAccess;
            }

            public void Dispose()
            {
                Stream?.Dispose();
            }

            public override string ToString() => DebuggerDisplay;

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Debugger Display")]
            [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
            private string DebuggerDisplay => $"{nameof(StreamContext)} {File.Name} [{Access}] [{nameof(Stream.Length)}={((Stream?.CanSeek ?? false) ? Stream.Length : 0)}] [{nameof(Task.Status)}={Task?.Status}] {nameof(IsLocked)}={IsLocked}".ToString(CultureInfo.CurrentCulture);
        }

        private ICloudDrive drive;

        private CloudDirectoryNode root;

        private ILogger logger;

        public CloudOperations(ICloudDrive drive, DirectoryInfoContract root, ILogger logger)
        {
            if (drive == null)
                throw new ArgumentNullException(nameof(drive));
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            this.drive = drive;
            this.root = new CloudDirectoryNode(root);
            this.logger = logger;
        }

        public CloudOperations(ICloudDrive drive, ILogger logger)
        {
            if (drive == null)
                throw new ArgumentNullException(nameof(drive));

            this.drive = drive;
            this.logger = logger;
        }

        private CloudItemNode GetItem(string fileName)
        {
            var result = root ?? (root = new CloudDirectoryNode(drive.GetRoot())) as CloudItemNode;

            var pathSegments = new Queue<string>(fileName.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries));

            while (result != null && pathSegments.Count > 0)
                result = (result as CloudDirectoryNode)?.GetChildItemByName(drive, pathSegments.Dequeue());

            return result;
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            if (info.DeleteOnClose) {
                (GetItem(fileName) as CloudFileNode)?.Remove(drive);
            } else if (!info.IsDirectory) {
                var context = info.Context as StreamContext;
                if (context?.CanWriteDelayed ?? false) {
                    context.Stream.Seek(0, SeekOrigin.Begin);
                    context.Task = Task.Run(() => {
                            try {
                                context.File.SetContent(drive, context.Stream);
                            } catch (Exception ex) {
                                if (!(ex is UnauthorizedAccessException) && !((uint)((ex as IOException)?.HResult ?? 0) == 0x80070020))
                                    context.File.Remove(drive);
                                logger?.Error($"{nameof(context.File.SetContent)} failed on file '{fileName}' with {ex.GetType().Name} '{ex.Message}'".ToString(CultureInfo.CurrentCulture));
                                throw;
                            }
                        })
                        .ContinueWith(t => logger?.Debug($"{nameof(context.File.SetContent)} finished on file '{fileName}'".ToString(CultureInfo.CurrentCulture)), TaskContinuationOptions.OnlyOnRanToCompletion);
                }

                if (context?.Task != null) {
                    context.Task.Wait();

                    if (context.Task.IsCompleted)
                        AsTrace(nameof(Cleanup), fileName, info, DokanResult.Success);
                    else
                        AsError(nameof(Cleanup), fileName, info, DokanResult.Error);
                    context.Dispose();
                    info.Context = null;
                    return;
                }
            }

            AsTrace(nameof(Cleanup), fileName, info, DokanResult.Success);
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            AsTrace(nameof(CloseFile), fileName, info, DokanResult.Success);

            var context = info.Context as StreamContext;
            context?.Dispose();
        }

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            // HACK: Fix for Bug in Dokany related to a missing trailing slash for directory names
            if (string.IsNullOrEmpty(fileName))
                fileName = @"\";
            // HACK: Fix for Bug in Dokany related to a call to CreateFile with a fileName of '\*'
            else if (fileName == @"\*" && access == FileAccess.ReadAttributes)
                return AsTrace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.Success);

            if (fileName == @"\") {
                info.IsDirectory = true;
                return AsTrace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.Success);
            }

            fileName = fileName.TrimEnd(Path.DirectorySeparatorChar);

            var parent = GetItem(Path.GetDirectoryName(fileName)) as CloudDirectoryNode;
            if (parent == null)
                return AsDebug(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.PathNotFound);

            var itemName = Path.GetFileName(fileName);
            var item = parent.GetChildItemByName(drive, itemName);
            var fileItem = default(CloudFileNode);
            switch (mode) {
                case FileMode.Create:
                    fileItem = item as CloudFileNode;
                    if (fileItem != null)
                        fileItem.Truncate(drive);
                    else
                        fileItem = parent.NewFileItem(drive, itemName);

                    info.Context = new StreamContext(fileItem, FileAccess.WriteData, originalAccess: access);

                    return AsTrace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.Success);
                case FileMode.Open:
                    fileItem = item as CloudFileNode;
                    if (fileItem != null) {
                        var realAccess = Normalize(access);
                        if (!realAccess.HasFlag(FileAccess.ReadAttributes) && !realAccess.HasFlag(FileAccess.ReadPermissions)
                            && !realAccess.HasFlag(FileAccess.ReadData) && !realAccess.HasFlag(FileAccess.WriteData)
                                                                           && !realAccess.HasFlag(FileAccess.Delete))
                            return AsDebug(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.NotImplemented);
                        info.Context = new StreamContext(fileItem, realAccess, originalAccess: access);
                    } else {
                        info.IsDirectory = item != null;
                    }

                    if (item != null)
                        return AsTrace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.Success);
                    else
                        return AsError(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.FileNotFound);
                case FileMode.OpenOrCreate:
                    fileItem = item as CloudFileNode ?? parent.NewFileItem(drive, itemName);

                    if (access.HasFlag(FileAccess.ReadData) && !access.HasFlag(FileAccess.WriteData))
                        info.Context = new StreamContext(fileItem, FileAccess.ReadData, originalAccess: access);
                    else
                        info.Context = new StreamContext(fileItem, FileAccess.WriteData, originalAccess: access);

                    return AsTrace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.Success);
                case FileMode.CreateNew:
                    if (item != null)
                        return AsDebug(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, info.IsDirectory ? DokanResult.AlreadyExists : DokanResult.FileExists);

                    if (info.IsDirectory) {
                        parent.NewDirectoryItem(drive, itemName);
                    } else {
                        fileItem = parent.NewFileItem(drive, itemName);

                        info.Context = new StreamContext(fileItem, FileAccess.WriteData, originalAccess: access);
                    }
                    return AsTrace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.Success);
                case FileMode.Append:
                    return AsError(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.NotImplemented);
                case FileMode.Truncate:
                    //fileItem = item as CloudFileNode;
                    //if (fileItem == null)
                    //    return AsDebug(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.FileNotFound);

                    //fileItem.Truncate(drive);

                    //info.Context = new StreamContext(fileItem, FileAccess.WriteData);

                    //return AsTrace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.Success);
                    return AsError(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.NotImplemented);
                default:
                    return AsError(nameof(CreateFile), fileName, info, access, share, mode, options, attributes, DokanResult.NotImplemented);
            }
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            var item = GetItem(fileName) as CloudDirectoryNode;
            if (item == null)
                return AsDebug(nameof(DeleteDirectory), fileName, info, DokanResult.PathNotFound);
            if (item.GetChildItems(drive).Any())
                return AsDebug(nameof(DeleteDirectory), fileName, info, DokanResult.DirectoryNotEmpty);

            item.Remove(drive);

            return AsTrace(nameof(DeleteDirectory), fileName, info, DokanResult.Success);
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var context = (StreamContext)info.Context;
            context.File.Remove(drive);

            return AsTrace(nameof(DeleteFile), fileName, info, DokanResult.Success);
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            if (GetItem(fileName) is not CloudDirectoryNode parent)
            {
                files = null;
                return NtStatus.ObjectPathNotFound;
            }

            var childItems = parent.GetChildItems(drive).Where(i => i.IsResolved).ToList();
            var infos = childItems.Select(i => ToFileInfo(i.Contract)).ToList();

            if (parent.Contract.Parent != null)
            {
                var selfInfo = ToFileInfo(parent.Contract);
                selfInfo.FileName = ".";
                var parentInfo = ToFileInfo(parent.Contract.Parent);
                parentInfo.FileName = "..";

                infos.InsertRange(0, new[] { selfInfo, parentInfo });
            }

            files = infos;

            return AsTrace(nameof(FindFiles), fileName, info, DokanResult.Success, $"out [{files.Count}]".ToString(CultureInfo.CurrentCulture));
        }

        static FileInformation ToFileInfo(FileSystemInfoContract infoContract)
        {
            return new FileInformation
            {
                FileName = infoContract.Name,
                Length = (infoContract as FileInfoContract)?.Size ?? FileSize.Empty,
                Attributes =
                    infoContract is DirectoryInfoContract
                        ? FileAttributes.Directory
                        : FileAttributes.NotContentIndexed,
                CreationTime = infoContract.Created.DateTime,
                LastWriteTime = infoContract.Updated.DateTime,
                LastAccessTime = infoContract.Updated.DateTime
            };
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
            IDokanFileInfo info)
        {
            files = null;
            return NtStatus.NotImplemented;
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = Enumerable.Empty<FileInformation>().ToList();
            return AsWarn(nameof(FindStreams), fileName, info, DokanResult.NotImplemented, $"out [{streams.Count}]".ToString(CultureInfo.CurrentCulture));
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            try {
                ((StreamContext)info.Context).Stream?.Flush();

                return AsTrace(nameof(FlushFileBuffers), fileName, info, DokanResult.Success);
            } catch (IOException) {
                return AsError(nameof(FlushFileBuffers), fileName, info, DokanResult.DiskFull);
            }
        }

        public NtStatus GetDiskFreeSpace(out long free, out long total, out long used, IDokanFileInfo info)
        {
            free = drive.Free ?? 0;
            used = drive.Used ?? 0;
            total = free + used;

            return AsTrace(nameof(GetDiskFreeSpace), null, info, DokanResult.Success, $"out {free}", $"out {total}", $"out {used}".ToString(CultureInfo.CurrentCulture));
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var item = GetItem(fileName);
            if (item == null) {
                fileInfo = default(FileInformation);
                return AsTrace(nameof(GetFileInformation), fileName, info, DokanResult.PathNotFound);
            }

            fileInfo = new FileInformation() {
                FileName = fileName, Length = (info.Context as StreamContext)?.Stream?.Length ?? (item as CloudFileNode)?.Contract.Size ?? FileSize.Empty,
                Attributes = item is CloudDirectoryNode ? FileAttributes.Directory : FileAttributes.NotContentIndexed,
                CreationTime = item.Contract.Created.DateTime, LastWriteTime = item.Contract.Updated.DateTime, LastAccessTime = item.Contract.Updated.DateTime
            };

            return AsTrace(nameof(GetFileInformation), fileName, info, DokanResult.Success, $"out {{{fileInfo.FileName}, [{fileInfo.Length}], [{fileInfo.Attributes}], {fileInfo.CreationTime}, {fileInfo.LastWriteTime}, {fileInfo.LastAccessTime}}}".ToString(CultureInfo.CurrentCulture));
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            security = info.IsDirectory
                ? new DirectorySecurity() as FileSystemSecurity
                : new FileSecurity() as FileSystemSecurity;
            security.AddAccessRule(new FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, AccessControlType.Allow));

            return AsTrace(nameof(GetFileSecurity), fileName, info, DokanResult.Success, $"out {security}", $"{sections}".ToString(CultureInfo.CurrentCulture));
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            volumeLabel = drive.DisplayRoot;
            features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
            fileSystemName = nameof(DokanCloudFS);
            maximumComponentLength = 250;

            return AsTrace(nameof(GetVolumeInformation), null, info, DokanResult.Success, $"out {volumeLabel}", $"out {features}", $"out {fileSystemName}".ToString(CultureInfo.CurrentCulture));
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var context = ((StreamContext)info.Context);

            if (context.IsLocked)
                return AsWarn(nameof(LockFile), fileName, info, DokanResult.AccessDenied, offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));

            context.IsLocked = true;
            return AsTrace(nameof(LockFile), fileName, info, DokanResult.Success, offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return AsTrace(nameof(Mounted), mountPoint, info, DokanResult.Success);
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            var item = GetItem(oldName);
            if (item == null)
                return AsWarn(nameof(MoveFile), oldName, info, DokanResult.FileNotFound, newName, replace.ToString(CultureInfo.InvariantCulture));

            var destinationDirectory = GetItem(Path.GetDirectoryName(newName)) as CloudDirectoryNode;
            if (destinationDirectory == null)
                return AsWarn(nameof(MoveFile), oldName, info, DokanResult.PathNotFound, newName, replace.ToString(CultureInfo.InvariantCulture));

            item.Move(drive, Path.GetFileName(newName), destinationDirectory, replace: replace);

            return AsTrace(nameof(MoveFile), oldName, info, DokanResult.Success, newName, replace.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus OpenDirectory(string fileName, IDokanFileInfo info)
        {
            var item = GetItem(fileName) as CloudDirectoryNode;
            if (item == null)
                return AsDebug(nameof(OpenDirectory), fileName, info, DokanResult.PathNotFound);

            return AsTrace(nameof(OpenDirectory), fileName, info, DokanResult.Success);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), string.Format(CultureInfo.CurrentCulture, Resources.NonnegativeValueRequired, offset));
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var context = (StreamContext)info.Context;

            lock (context) {
                if (context.Stream == null)
                    try {
                        context.Stream = Stream.Synchronized(context.File.GetContent(drive));
                    } catch (Exception ex) {
                        bytesRead = 0;
                        return AsError(nameof(ReadFile), fileName, info, DokanResult.Error, offset.ToString(CultureInfo.InvariantCulture), $"out {bytesRead}".ToString(CultureInfo.InvariantCulture), $"{ex.GetType().Name} '{ex.Message}'".ToString(CultureInfo.CurrentCulture));
                    }

                context.Stream.Position = offset;
                bytesRead = context.Stream.Read(buffer, 0, buffer.Length);
            }

            return AsDebug(nameof(ReadFile), fileName, info, DokanResult.Success, offset.ToString(CultureInfo.InvariantCulture), $"out {bytesRead}".ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var context = (StreamContext)info.Context;
            lock (context)
            {
                if (context.Stream == null) {
                    var gatherStreams = new Stream[2];
                    ScatterGatherStreamFactory.CreateScatterGatherStreams(checked((int)length), out var scatterStream, gatherStreams);

                    context.Stream = new ReadWriteSegregatingStream(scatterStream, gatherStreams[1]);

                    context.Task = Task.Run(() => {
                            try {
                                context.File.SetContent(drive, gatherStreams[0]);
                            } catch (Exception ex) {
                                if (!(ex is UnauthorizedAccessException))
                                    context.File.Remove(drive);
                                logger.Error($"{nameof(context.File.SetContent)} failed on file '{fileName}' with {ex.GetType().Name} '{ex.Message}'".ToString(CultureInfo.CurrentCulture));
                                throw;
                            }
                        })
                        .ContinueWith(t => logger.Debug($"{nameof(context.File.SetContent)} finished on file '{fileName}'".ToString(CultureInfo.CurrentCulture)), TaskContinuationOptions.OnlyOnRanToCompletion);
                } else {
                    var scatterStream = (context.Stream as ReadWriteSegregatingStream)?.WriteStream as ScatterStream;
                    if (scatterStream != null)
                        scatterStream.Capacity = (int)length;
                }
            }

            return AsDebug(nameof(SetAllocationSize), fileName, info, DokanResult.Success, length.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            var context = ((StreamContext)info.Context);

            lock (context) {
                if (context.Stream == null)
                {
                    context.Stream = Stream.Synchronized(new MemoryStream());
                }

                try
                {
                    context.Stream.SetLength(length);
                }
                catch (NotSupportedException)
                {
                    AsError(nameof(SetEndOfFile), fileName, info, DokanResult.NotImplemented, length.ToString());
                }
            }

            return AsDebug(nameof(SetEndOfFile), fileName, info, DokanResult.Success, length.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            // TODO: Possibly return NotImplemented here
            return AsDebug(nameof(SetFileAttributes), fileName, info, DokanResult.Success, attributes.ToString());
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            return AsDebug(nameof(SetFileAttributes), fileName, info, DokanResult.NotImplemented, sections.ToString());
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            // TODO: Possibly return NotImplemented here
            return AsDebug(nameof(SetFileTime), fileName, info, DokanResult.Success, creationTime.ToString(), lastAccessTime.ToString(), lastWriteTime.ToString());
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var context = ((StreamContext)info.Context);
            if (!context.IsLocked)
                return AsWarn(nameof(UnlockFile), fileName, info, DokanResult.AccessDenied, offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));

            context.IsLocked = false;
            return AsTrace(nameof(UnlockFile), fileName, info, DokanResult.Success, offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var result = AsTrace(nameof(Unmounted), null, info, DokanResult.Success);

            drive = null;
            logger = null;

            return result;
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Resources.NonnegativeValueRequired, offset), nameof(offset));
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var context = ((StreamContext)info.Context);

            lock (context) {
                if (context.Stream == null)
                    context.Stream = Stream.Synchronized(new MemoryStream());

                context.Stream.Position = offset;
                context.Stream.Write(buffer, 0, buffer.Length);
                bytesWritten = (int)(context.Stream.Position - offset);
            }

            return AsDebug(nameof(WriteFile), fileName, info, DokanResult.Success, offset.ToString(CultureInfo.InvariantCulture), $"out {bytesWritten}".ToString(CultureInfo.InvariantCulture));
        }

        static FileAccess Normalize(FileAccess access)
        {
            var normalized = access;
            if (access.HasFlag(FileAccess.GenericRead))
                normalized |= FileAccess.ReadData;
            if (access.HasFlag(FileAccess.GenericWrite))
                normalized |= FileAccess.WriteData;
            if (access.HasFlag(FileAccess.GenericAll) || access.HasFlag(FileAccess.MaximumAllowed))
                normalized |= FileAccess.ReadData | FileAccess.ReadAttributes
                                                  | FileAccess.WriteData | FileAccess.WriteAttributes;
            return normalized;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Debugger Display")]
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{nameof(CloudOperations)} drive={drive} root={root}".ToString(CultureInfo.CurrentCulture);
    }
}