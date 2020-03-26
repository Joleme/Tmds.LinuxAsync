using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    sealed class AwaitableSocketReceiveOperation : AsyncReceiveOperation, IValueTaskSource<int>
    {
        private AwaitableSocketOperationCore<int> _core;

        public short Version => _core.Version;

        public AwaitableSocketReceiveOperation()
            => _core.Init();

        public void RegisterCancellation(CancellationToken cancellationToken)
            => _core.RegisterCancellation(cancellationToken);

        public int GetResult(short token)
        {
            var result = _core.GetResult(token);
            ResetAndReturnThis();
            return result.GetValue();
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);

        public override void Complete()
        {
            int bytesTransferred = BytesTransferred;
            SocketError socketError = SocketError;
            OperationStatus status = Status;
            ResetOperationState();

            if ((status & OperationStatus.CancelledSync) == OperationStatus.CancelledSync)
            {
                // Caller threw an exception which prevents further use of this.
                ResetAndReturnThis();
            }
            else
            {
                _core.SetResult(bytesTransferred, socketError, status);
            }
        }

        private void ResetAndReturnThis()
        {
            _core.Reset();
            ReturnThis();
        }
    }

    sealed class SaeaReceiveOperation : AsyncReceiveOperation, IThreadPoolWorkItem
    {
        public readonly SocketAsyncEventArgs _saea;
        public SaeaReceiveOperation(SocketAsyncEventArgs saea)
        {
            _saea = saea;
        }

        public override void Complete()
        {
            SetSaeaResult();
            ResetOperationState();

            bool runContinuationsAsync = _saea.RunContinuationsAsynchronously;
            bool completeSync = (Status & OperationStatus.Sync) != 0;
            if (completeSync || !runContinuationsAsync)
            {
                ((IThreadPoolWorkItem)this).Execute();
            }
            else
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        private void SetSaeaResult()
        {
            _saea.SocketError = SocketError;
            _saea.BytesTransferred = BytesTransferred;
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Capture state.
            OperationStatus completionStatus = Status;

            // Reset state.
            Status = OperationStatus.None;
            CurrentAsyncContext = null;

            // Complete.
            _saea.Complete(completionStatus);
        }
    }

    // Base class for AsyncSocketEventArgsOperation and AwaitableSocketOperation.
    // Implements operation execution.
    // Derived classes implement completion.
    abstract class AsyncReceiveOperation : AsyncOperation
    {
        private Socket? Socket;
        private Memory<byte> MemoryBuffer;
        protected SocketError SocketError;
        protected int BytesTransferred;

        public void Configure(Socket socket, Memory<byte> memory, IList<ArraySegment<byte>>? bufferList)
        {
            if (bufferList != null)
            {
                throw new NotSupportedException();
            }

            Socket = socket;
            MemoryBuffer = memory;
        }

        public override bool IsReadNotWrite => true;

        public override bool TryExecuteSync()
        {
            Socket socket = Socket!;
            (SocketError socketError, int bytesTransferred) = SocketPal.Recv(socket.SafeHandle, MemoryBuffer);
            BytesTransferred = bytesTransferred;
            if (socketError == SocketError.WouldBlock)
            {
                return false;
            }
            SocketError = socketError;
            return true;
        }

        public override AsyncExecutionResult TryExecuteAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data)
        {
            Memory<byte> memory = MemoryBuffer;
            bool isPollingReadable = memory.Length == 0; // A zero-byte read is a poll.
            if (executionQueue != null && (!isPollingReadable || executionQueue.SupportsPolling == true))
            {
                Socket socket = Socket!;
                executionQueue.AddRead(socket.SafeHandle, MemoryBuffer, callback!, state, data);
                return AsyncExecutionResult.Executing;
            }
            else
            {
                if (triggeredByPoll && isPollingReadable)
                {
                    // No need to make a syscall, poll told us we're readable.
                    SocketError = SocketError.Success;
                    BytesTransferred = 0;
                    return AsyncExecutionResult.Finished;
                }
                bool finished = TryExecuteSync();
                return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WaitForPoll;
            }
        }

        public override AsyncExecutionResult HandleAsyncResultAndContinue(AsyncOperationResult asyncResult, AsyncExecutionQueue executionQueue, AsyncExecutionCallback? callback, object? state, int data)
        {
            AsyncExecutionResult result = HandleAsyncResult(asyncResult);

            if (result == AsyncExecutionResult.Finished)
            {
                return AsyncExecutionResult.Finished;
            }

            if (result == AsyncExecutionResult.Cancelled || IsCancellationRequested)
            {
                return AsyncExecutionResult.Cancelled;
            }

            if (result == AsyncExecutionResult.WaitForPoll && executionQueue?.SupportsPolling != true)
            {
                return AsyncExecutionResult.WaitForPoll;
            }

            return TryExecuteAsync(triggeredByPoll: false, executionQueue, callback, state, data);
        }

        private AsyncExecutionResult HandleAsyncResult(AsyncOperationResult asyncResult)
        {
            if (asyncResult.IsError)
            {
                if (asyncResult.Errno == EINTR)
                {
                    return AsyncExecutionResult.Executing;
                }
                else if (asyncResult.Errno == ECANCELED)
                {
                    return AsyncExecutionResult.Cancelled;
                }
                else if (asyncResult.Errno == EAGAIN)
                {
                    return AsyncExecutionResult.WaitForPoll;
                }
                else
                {
                    SocketError = SocketPal.GetSocketErrorForErrno(asyncResult.Errno);
                    return AsyncExecutionResult.Finished;
                }
            }
            else
            {
                BytesTransferred = asyncResult.IntValue;
                SocketError = SocketError.Success;
                return AsyncExecutionResult.Finished;
            }
        }

        protected void ResetOperationState()
        {
            Socket = null;
            MemoryBuffer = default;
            SocketError = SocketError.SocketError;
            BytesTransferred = 0;
        }
    }
}