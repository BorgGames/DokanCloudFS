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
using IgorSoft.CloudFS.Interfaces.IO;
#if COMPOSITION
using IgorSoft.DokanCloudFS.Composition;
#endif
using IgorSoft.DokanCloudFS.IO;
using IgorSoft.DokanCloudFS.Parameters;

namespace IgorSoft.DokanCloudFS
{
    [System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
    public sealed class AsyncCloudDrive : CloudDriveBase, ICloudDrive
    {
        private readonly IAsyncCloudGateway gateway;

        private readonly IDictionary<string, string> parameters;

        public AsyncCloudDrive(RootName rootName, IAsyncCloudGateway gateway, CloudDriveParameters parameters) : base(rootName, parameters.ThreadSafeGateway)
        {
            this.gateway = gateway;
            this.parameters = parameters.Parameters;
        }

        public IPersistGatewaySettings PersistSettings => gateway as IPersistGatewaySettings;

        protected override DriveInfoContract GetDrive()
        {
            try {
                var tmp = drive;
                if (tmp == null) {
                    tmp = gateway.GetDriveAsync(rootName, null, parameters).Result;
                    tmp.Name = DisplayRoot + Path.VolumeSeparatorChar;
                    drive = tmp;
                }
                return tmp;
            } catch (AggregateException ex) when (ex.InnerExceptions.Count == 1) {
                throw ex.InnerExceptions[0];
            }
        }

        public bool TryAuthenticate()
        {
            return gateway.TryAuthenticateAsync(rootName, null, parameters).Result;
        }

        public RootDirectoryInfoContract GetRoot()
        {
            return ExecuteInSemaphore(() => {
                var tmp = GetDrive();
                var root = gateway.GetRootAsync(rootName, null, parameters).Result;
                root.Drive = tmp;
                return root;
            }, nameof(GetRoot));
        }

        public IEnumerable<FileSystemInfoContract> GetChildItem(DirectoryInfoContract parent)
        {
            return ExecuteInSemaphore(() => {
                return gateway.GetChildItemAsync(rootName, parent.Id).Result;
            }, nameof(GetChildItem));
        }

        public Stream GetContent(FileInfoContract source)
        {
            return ExecuteInSemaphore(() => {
                var gatewayContent = gateway.GetContentAsync(rootName, source.Id).Result.ToSeekableStream();

                var content = gatewayContent;
                if (content != gatewayContent)
                    gatewayContent.Close();
                source.Size = (FileSize)content.Length;

#if DEBUG && COMPOSITION
                CompositionInitializer.SatisfyImports(content = new TraceStream(nameof(GetContent), source.Name, content));
#endif
                return content;
            }, nameof(GetContent));
        }

        public void SetContent(FileInfoContract target, Stream content)
        {
            ExecuteInSemaphore(() => {
                var gatewayContent = content;
                target.Size = (FileSize)content.Length;

#if DEBUG && COMPOSITION
                CompositionInitializer.SatisfyImports(gatewayContent = new TraceStream(nameof(SetContent), target.Name, gatewayContent));
#endif
                Func<FileSystemInfoLocator> locator = () => new FileSystemInfoLocator(target);
                try
                {
                    gateway.SetContentAsync(rootName, target.Id, gatewayContent, null, locator).Wait();
                }
                finally
                {
                    InvalidateDrive();
                }
                if (content != gatewayContent)
                    gatewayContent.Close();
            }, nameof(SetContent));
        }

        public FileSystemInfoContract MoveItem(FileSystemInfoContract source, string movePath, DirectoryInfoContract destination, bool replace)
        {
            return ExecuteInSemaphore(() => {
                Func<FileSystemInfoLocator> locator = () => new FileSystemInfoLocator(source);
                try
                {
                    return gateway.MoveItemAsync(rootName, source.Id, movePath, destination.Id, replace: replace, locator).Result;
                }
                finally
                {
                    InvalidateDrive();
                }
            }, nameof(MoveItem));
        }

        public DirectoryInfoContract NewDirectoryItem(DirectoryInfoContract parent, string name)
        {
            return ExecuteInSemaphore(() =>
            {
                try
                {
                    return gateway.NewDirectoryItemAsync(rootName, parent.Id, name).Result;
                }
                finally
                {
                    InvalidateDrive();
                }
            }, nameof(NewDirectoryItem));
        }

        public FileInfoContract NewFileItem(DirectoryInfoContract parent, string name, Stream content)
        {
            return ExecuteInSemaphore(() => {
                var gatewayContent = content;

                try
                {
                    var result = gateway.NewFileItemAsync(rootName, parent.Id, name, gatewayContent, null).Result;
                    result.Size = (FileSize)content.Length;
                    return result;
                }
                finally
                {
                    InvalidateDrive();
                }
            }, nameof(NewFileItem));
        }

        public void RemoveItem(FileSystemInfoContract target, bool recurse)
        {
            ExecuteInSemaphore(() => {
                if (!(target is ProxyFileInfoContract))
                    try
                    {
                        gateway.RemoveItemAsync(rootName, target.Id, recurse).Wait();
                    }
                    finally
                    {
                        InvalidateDrive();
                    }
            }, nameof(RemoveItem));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Debugger Display")]
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{nameof(AsyncCloudDrive)} {DisplayRoot}".ToString(CultureInfo.CurrentCulture);
    }
}
