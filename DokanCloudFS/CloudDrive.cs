/*
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

using IgorSoft.CloudFS.Interfaces;
using IgorSoft.CloudFS.Interfaces.Composition;
using IgorSoft.CloudFS.Interfaces.IO;
#if COMPOSITION
using IgorSoft.DokanCloudFS.Composition;
#endif
using IgorSoft.DokanCloudFS.IO;
using IgorSoft.DokanCloudFS.Parameters;

namespace IgorSoft.DokanCloudFS
{
    [System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
    internal class CloudDrive : CloudDriveBase, ICloudDrive
    {
        private readonly ICloudGateway gateway;

        private readonly IDictionary<string, string> parameters;

        public CloudDrive(RootName rootName, ICloudGateway gateway, CloudDriveParameters parameters) : base(rootName, parameters.ThreadSafeGateway)
        {
            this.gateway = gateway;
            this.parameters = parameters.Parameters;
        }

        public IPersistGatewaySettings PersistSettings => gateway as IPersistGatewaySettings;

        protected override DriveInfoContract GetDrive()
        {
            var tmp = drive;
            if (tmp == null) {
                tmp = gateway.GetDrive(rootName, null, parameters);
                tmp.Name = DisplayRoot + Path.VolumeSeparatorChar;
                drive = tmp;
            }
            return tmp;
        }

        public bool TryAuthenticate()
        {
            return gateway.TryAuthenticate(rootName, null, parameters);
        }

        public RootDirectoryInfoContract GetRoot()
        {
            return ExecuteInSemaphore(() => {
                var tmp = GetDrive();
                var root = gateway.GetRoot(rootName, null, parameters);
                root.Drive = tmp;
                return root;
            });
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Language", "CSE0003:Use expression-bodied members")]
        public IEnumerable<FileSystemInfoContract> GetChildItem(DirectoryInfoContract parent)
        {
            return ExecuteInSemaphore(() => {
                return gateway.GetChildItem(rootName, parent.Id);
            });
        }

        public Stream GetContent(FileInfoContract source)
        {
            return ExecuteInSemaphore(() => {
                var gatewayContent = gateway.GetContent(rootName, source.Id).ToSeekableStream();

                var content = gatewayContent;
                if (content != gatewayContent)
                    gatewayContent.Close();
                source.Size = (FileSize)content.Length;

#if DEBUG && COMPOSITION
                CompositionInitializer.SatisfyImports(content = new TraceStream(nameof(source), source.Name, content));
#endif
                return content;
            });
        }

        public void SetContent(FileInfoContract target, Stream content)
        {
            ExecuteInSemaphore(() => {
                var gatewayContent = content;
                target.Size = (FileSize)content.Length;

#if DEBUG && COMPOSITION
                CompositionInitializer.SatisfyImports(gatewayContent = new TraceStream(nameof(target), target.Name, gatewayContent));
#endif
                try
                {
                    gateway.SetContent(rootName, target.Id, gatewayContent, null);
                    if (content != gatewayContent)
                        gatewayContent.Close();
                }
                finally
                {
                    InvalidateDrive();
                }
            });
        }

        public FileSystemInfoContract MoveItem(FileSystemInfoContract source, string movePath, DirectoryInfoContract destination, bool replace)
        {
            return ExecuteInSemaphore(() => {
                try
                {
                    return gateway.MoveItem(rootName, source.Id, movePath, destination.Id);
                }
                finally
                {
                    InvalidateDrive();
                }
            });
        }

        public DirectoryInfoContract NewDirectoryItem(DirectoryInfoContract parent, string name)
        {
            return ExecuteInSemaphore(() => {
                try
                {
                    return gateway.NewDirectoryItem(rootName, parent.Id, name);
                }
                finally
                {
                    InvalidateDrive();
                }
            });
        }

        public FileInfoContract NewFileItem(DirectoryInfoContract parent, string name, Stream content)
        {
            return ExecuteInSemaphore(() => {
                try
                {
                    var result = gateway.NewFileItem(rootName, parent.Id, name, content, null);
                    result.Size = (FileSize)content.Length;
                    return result;
                }
                finally
                {
                    InvalidateDrive();
                }
            });
        }

        public void RemoveItem(FileSystemInfoContract target, bool recurse)
        {
            ExecuteInSemaphore(() => {
                if (!(target is ProxyFileInfoContract))
                    try
                    {
                        gateway.RemoveItem(rootName, target.Id, recurse);
                    }
                    finally
                    {
                        InvalidateDrive();
                    }
            });
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Debugger Display")]
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{nameof(CloudDrive)} {DisplayRoot}".ToString(CultureInfo.CurrentCulture);
    }
}
