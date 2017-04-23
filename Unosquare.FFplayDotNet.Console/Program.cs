using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unosquare.Swan;

namespace Unosquare.FFplayDotNet.Console
{
    class Program
    {
        static void Main(string[] args)
        {
            var t1 = new Thread(ReaderDoWork) { IsBackground = true };
            var t2 = new Thread(WriterDoWork) { IsBackground = true };

            t1.Start();
            t2.Start();

            Terminal.ReadKey(true, true);
            t1.Abort();
            t2.Abort();
        }

        private static readonly MonitorLock SyncLock = new MonitorLock();
        private static readonly LockCondition DoneWriting = new LockCondition();
        private static readonly List<int> List = new List<int>();

        private static void ReaderDoWork()
        {
            while (true)
            {
                SyncLock.Lock();
                DoneWriting.Wait(SyncLock);
                $"Reader: List has {List.Count} elements. The Last Element is {List.Last()}".Info();
                SyncLock.Unlock();
            }
        }

        private static void WriterDoWork()
        {
            while (true)
            {
                SyncLock.Lock();
                var numberToWrite = List.Count + 1;
                List.Add(numberToWrite);
                DoneWriting.Signal();
                $"Writer: Write Cycle Ended. Wrote number {numberToWrite}".Info();
                Thread.Sleep(1000);
                SyncLock.Unlock();
                
            }
        }
    }
}
