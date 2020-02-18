using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IoUring;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class IOUringAsyncEngine
    {
        sealed class IOUringExecutionQueue : AsyncExecutionQueue
        {
            private ulong MaskBit = 1UL << 63;
            private const int MemoryAlignment = 8;
            private const int SubmissionQueueLength = 512; // TODO
            // private const int CompletionQueueLength = CompletionQueueLength; // TODO
            Ring? _ring;

            enum OperationType
            {
                Read,
                Write,
                PollIn,
                PollOut
            }

            // TODO: maybe make this an interface that is implemented by a (read/write) Queue class
            //       (owned by the AsyncContext) which then gets added as an operation.
            class Operation
            {
                public OperationType OperationType;
                public SafeHandle? Handle;

                public Memory<byte> Memory;
                public MemoryHandle MemoryHandle;

                public AsyncExecutionCallback? Callback;
                public object? State;

                public int Data;
            }

            private Dictionary<ulong, Operation> _operations;
            private List<Operation> _newOperations;
            private readonly Stack<Operation> _operationPool;
            private int _newOperationsQueued; // Number of operations added to submission queue, not yet submitted.
            private uint _sqesQueued; // Number of free entries in the submission queue.
            private int _iovsLength;
            private bool _disposed;
            private readonly IntPtr _ioVectorTableMemory;
            private unsafe iovec* IoVectorTable => (iovec*)Align(_ioVectorTableMemory);

            public unsafe IOUringExecutionQueue() :
                base(supportsPolling: true)
            {
                _operationPool = new Stack<Operation>();
                _operations = new Dictionary<ulong, Operation>();
                _newOperations = new List<Operation>();
                try
                {
                    _ring = new Ring(SubmissionQueueLength);
                    if (!_ring.SupportsNoDrop)
                    {
                        throw new NotSupportedException("io_uring IORING_FEAT_NODROP is needed.");
                    }
                    if (!_ring.SupportsStableSubmits)
                    {
                        throw new NotSupportedException("io_uring IORING_FEAT_SUBMIT_STABLE is needed.");
                    }
                    _iovsLength = _ring.SubmissionQueueSize; // TODO
                    _ioVectorTableMemory = AllocMemory(SizeOf.iovec * _iovsLength);
                }
                catch
                {
                    FreeResources();
                }
            }

            public override void AddRead(SafeHandle handle, Memory<byte> memory, AsyncExecutionCallback callback, object? state, int data)
            {
                // TODO: maybe consider writing directly to the sq
                //       This requires handling sq full
                //       which may require handling completions
                //       which means we should no longer call this under a lock from the AsyncContext...
                ulong key = CalculateKey(handle, data);
                Operation operation = RentOperation();
                operation.Handle = handle;
                operation.Memory = memory;
                operation.OperationType = OperationType.Read;
                operation.Callback = callback;
                operation.State = state;
                operation.Data = data;
                AddNewOperation(key, operation);
            }

            public override void AddWrite(SafeHandle handle, Memory<byte> memory, AsyncExecutionCallback callback, object? state, int data)
            {
                ulong key = CalculateKey(handle, data);
                Operation operation = RentOperation();
                operation.Handle = handle;
                operation.Memory = memory;
                operation.OperationType = OperationType.Write;
                operation.Callback = callback;
                operation.State = state;
                operation.Data = data;
                AddNewOperation(key, operation);
            }

            public override void AddPollIn(SafeHandle handle, AsyncExecutionCallback callback, object? state, int data)
            {
                ulong key = CalculateKey(handle, data);
                Operation operation = RentOperation();
                operation.Handle = handle;
                operation.OperationType = OperationType.PollIn;
                operation.Callback = callback;
                operation.State = state;
                operation.Data = data;
                AddNewOperation(key, operation);
            }

            public override void AddPollOut(SafeHandle handle, AsyncExecutionCallback callback, object? state, int data)
            {
                ulong key = CalculateKey(handle, data);
                Operation operation = RentOperation();
                operation.Handle = handle;
                operation.OperationType = OperationType.PollOut;
                operation.Callback = callback;
                operation.State = state;
                operation.Data = data;
                AddNewOperation(key, operation);
            }

            private void AddNewOperation(ulong key, Operation operation)
            {
                _operations.Add(key, operation);
                _newOperations.Add(operation);
            }

            private unsafe bool WriteSubmissions()
            {
                Ring ring = _ring!;
                if (_sqesQueued == 0)
                {
                    // We clear _newOperationsQueued when all sqes got submitted.
                    // We don't add new operations until we submitted all sqes,
                    // because we don't track how many sqes were added per operation.
                    Debug.Assert(_newOperationsQueued == 0);

                    int iovIndex = 0;
                    int sqesAvailable = ring.SubmissionQueueSize - (int)_sqesQueued;
                    iovec* iovs = IoVectorTable;
                    for (int i = 0; (i < _newOperations.Count) && (sqesAvailable > 2) && (iovIndex < _iovsLength); i++)
                    {
                        _newOperationsQueued++;

                        Operation op = _newOperations[i];
                        int fd = op.Handle!.DangerousGetHandle().ToInt32();
                        ulong key = CalculateKey(op.Handle, op.Data);
                        switch (op.OperationType)
                        {
                            case OperationType.Read:
                                {
                                    MemoryHandle handle = op.Memory.Pin();
                                    op.MemoryHandle = handle;
                                    iovec* iov = &iovs[iovIndex++];
                                    *iov = new iovec { iov_base = handle.Pointer, iov_len = op.Memory.Length };
                                    sqesAvailable -= 2;
                                    // Poll first, in case the fd is non-blocking.
                                    ring.PreparePollAdd(fd, (ushort)POLLIN, key | MaskBit, options: SubmissionOption.Link);
                                    ring.PrepareReadV(fd, iov, 1, userData: key);
                                    break;
                                }
                            case OperationType.Write:
                                {
                                    MemoryHandle handle = op.Memory.Pin();
                                    op.MemoryHandle = handle;
                                    iovec* iov = &iovs[iovIndex++];
                                    *iov = new iovec { iov_base = handle.Pointer, iov_len = op.Memory.Length };
                                    sqesAvailable -= 2;
                                    // Poll first, in case the fd is non-blocking.
                                    ring.PreparePollAdd(fd, (ushort)POLLOUT, key | MaskBit, options: SubmissionOption.Link);
                                    ring.PrepareWriteV(fd, iov, 1, userData: key);
                                    break;
                                }
                            case OperationType.PollIn:
                                {
                                    sqesAvailable -= 1;
                                    ring.PreparePollAdd(fd, (ushort)POLLIN, key);
                                    break;
                                }
                            case OperationType.PollOut:
                                {
                                    sqesAvailable -= 1;
                                    ring.PreparePollAdd(fd, (ushort)POLLOUT, key);
                                    break;
                                }
                        }
                    }
                    _sqesQueued = (uint)(ring.SubmissionQueueSize - sqesAvailable);
                }

                bool operationsRemaining = (_newOperations.Count - _newOperationsQueued) > 0;
                return operationsRemaining;
            }

            public unsafe void SubmitAndWait(Func<object, bool> mayWait, object mayWaitState)
            {
                try
                {
                    bool operationsRemaining;
                    do
                    {
                        operationsRemaining = WriteSubmissions();

                        // We can't wait if there are more submissions to be sent,
                        // or the event loop wants to do something.
                        bool waitForCompletion = !operationsRemaining && mayWait(mayWaitState);

                        // io_uring_enter
                        _ring!.Submit();
                        uint submitted = _ring!.Flush((uint)_sqesQueued, minComplete: waitForCompletion ? 1U : 0);

                        // only when the kernel runs out of resources (unlikely), we'll not be able to submit all requests.
                        if (submitted == _sqesQueued)
                        {
                            _sqesQueued = 0;
                            _newOperationsQueued = 0;
                            _newOperations.Clear();
                        }
                        else
                        {
                            // TODO: This seems similar to EAGAIN, not enough resources?
                            // Or does it happen in other cases?
                            // Is there a semantical difference between 0 and EAGAIN;
                            // could submitted be less than _seqsQueued if there is an issue with
                            // the sqe at submitted + 1?
                            break;
                        }
                    } while (operationsRemaining);
                }
                catch (ErrnoException ex) when (ex.Errno == EBUSY || // The application needs to read completions.
                                                ex.Errno == EAGAIN)  // The kernel doesn't have enough resources.
                { }
            }

            public void ExecuteCompletions()
            {
                while (_ring!.TryRead(out Completion completion))
                {
                    ulong key = completion.userData;
                    if (_operations.Remove(key, out Operation? op))
                    {
                        // Clean up
                        op.MemoryHandle.Dispose();

                        // Capture state
                        object? state = op.State;
                        int data = op.Data;
                        AsyncExecutionCallback callback = op.Callback!;

                        // Return the operation
                        ReturnOperation(op);

                        // Complete
                        callback(new AsyncOperationResult(completion.result), state, data);
                    }
                    else
                    {
                        Debug.Assert((key & (1UL << 63)) != 0);
                    }
                }
            }

            protected unsafe override void Dispose(bool disposing)
            {
                // TODO: complete pending operations.

                FreeResources();
            }

            private unsafe void FreeResources()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                _ring?.Dispose();

                if (_ioVectorTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ioVectorTableMemory);
                }
            }

            private ulong CalculateKey(SafeHandle handle, int data)
            {
                unchecked
                {
                    ulong fd = (ulong)handle.DangerousGetHandle().ToInt32();
                    ulong d = (ulong)data;
                    return (fd << 32) | d;
                }
            }

            private unsafe void* Align(IntPtr p)
            {
                ulong pointer = (ulong)p;
                pointer += MemoryAlignment - 1;
                pointer &= ~(ulong)(MemoryAlignment - 1);
                return (void*)pointer;
            }

            private unsafe IntPtr AllocMemory(int length)
            {
                IntPtr res = Marshal.AllocHGlobal(length + MemoryAlignment - 1);
                Span<byte> span = new Span<byte>(Align(res), length);
                span.Clear();
                return res;
            }

            private Operation RentOperation()
            {
                if (!_operationPool.TryPop(out Operation? result))
                {
                    result = new Operation();
                }
                return result;
            }

            private void ReturnOperation(Operation operation)
            {
                _operationPool.Push(operation);
            }
        }
    }
}