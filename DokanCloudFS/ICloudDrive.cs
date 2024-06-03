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
using System.IO;
using System.Threading;

using IgorSoft.CloudFS.Interfaces;
using IgorSoft.CloudFS.Interfaces.IO;

namespace IgorSoft.DokanCloudFS
{
    public interface ICloudDrive : IDisposable
    {
        string DisplayRoot { get; }

        long? Free { get; }

        long? Used { get; }

        IPersistGatewaySettings PersistSettings { get; }

        bool SupportsCancellation { get; }

        bool TryAuthenticate(CancellationToken cancel = default);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        RootDirectoryInfoContract GetRoot(CancellationToken cancel = default);

        IEnumerable<FileSystemInfoContract> GetChildItem(DirectoryInfoContract parent, CancellationToken cancel = default);

        Stream GetContent(FileInfoContract source, CancellationToken cancel = default);

        void SetContent(FileInfoContract target, Stream content, CancellationToken cancel = default);

        FileSystemInfoContract MoveItem(FileSystemInfoContract source, string movePath, DirectoryInfoContract destination, bool replace, CancellationToken cancel = default);

        DirectoryInfoContract NewDirectoryItem(DirectoryInfoContract parent, string name, CancellationToken cancel = default);

        FileInfoContract NewFileItem(DirectoryInfoContract parent, string name, Stream content, CancellationToken cancel = default);

        void RemoveItem(FileSystemInfoContract target, bool recurse, CancellationToken cancel = default);
    }
}