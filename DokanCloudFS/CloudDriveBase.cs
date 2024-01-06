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
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using IgorSoft.CloudFS.Interfaces;
using IgorSoft.CloudFS.Interfaces.IO;

namespace IgorSoft.DokanCloudFS
{
    public abstract class CloudDriveBase : IDisposable
    {
        protected readonly RootName rootName;

        protected volatile DriveInfoContract drive;

        protected readonly bool threadSafeGateway;

        private SemaphoreSlim semaphore = new SemaphoreSlim(1);

        public string DisplayRoot { get; }

        public long? Free => ExecuteInSemaphore(() => GetDrive().FreeSpace, $"get_{nameof(Free)}".ToString(CultureInfo.InvariantCulture));

        public long? Used => ExecuteInSemaphore(() => GetDrive().UsedSpace, $"get_{nameof(Used)}".ToString(CultureInfo.InvariantCulture));

        protected CloudDriveBase(RootName rootName, bool threadSafeGateway)
        {
            this.rootName = rootName;
            DisplayRoot = rootName.Value;
            this.threadSafeGateway = threadSafeGateway;
        }

        protected void ExecuteInSemaphore(Action action, [CallerMemberName] string methodName = null)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (threadSafeGateway)
            {
                action();
                return;
            }

            semaphore.Wait();
            try {
                action();
            } catch (AggregateException ex) when (ex.InnerExceptions.Count == 1) {
                throw ex.InnerExceptions[0];
            } finally {
                semaphore.Release();
            }
        }

        protected T ExecuteInSemaphore<T>(Func<T> func, [CallerMemberName] string methodName = null)
        {
            if (func == null)
                throw new ArgumentNullException(nameof(func));

            if (threadSafeGateway)
                return func();

            semaphore.Wait();
            try {
                return func();
            } catch (AggregateException ex) when (ex.InnerExceptions.Count == 1) {
                throw ex.InnerExceptions[0];
            } finally {
                semaphore.Release();
            }
        }

        protected void InvalidateDrive() => Interlocked.Exchange(ref drive, null);

        protected abstract DriveInfoContract GetDrive();

        public void Dispose()
        {
            semaphore.Dispose();
            semaphore = null;
            GC.SuppressFinalize(this);
        }
    }
}
