using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace HttpBench
{
    /// <summary>
    /// An listener for Kestrel that operates completely in memory.
    /// </summary>
    sealed class InMemoryListenerFactory : IConnectionListenerFactory
    {
        readonly Channel<TaskCompletionSource<ConnectionContext>> _acceptRequests = Channel.CreateUnbounded<TaskCompletionSource<ConnectionContext>>();

        public async ValueTask<DuplexPipeStream> ConnectClientAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            var ctx = new InMemoryConnectionContext();

            while (true)
            {
                if (!await _acceptRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new TaskCanceledException($"The {nameof(InMemoryListenerFactory)} was disposed.");
                }

                while (_acceptRequests.Reader.TryRead(out TaskCompletionSource<ConnectionContext> accept))
                {
                    if (accept.TrySetResult(ctx))
                    {
                        return ctx.ClientTransport;
                    }
                }
            }
        }

        private async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken)
        {
            var acceptRequest = new TaskCompletionSource<ConnectionContext>(TaskContinuationOptions.RunContinuationsAsynchronously);

            using (cancellationToken.UnsafeRegister(_ => { acceptRequest.TrySetCanceled(cancellationToken); }, null))
            {
                await _acceptRequests.Writer.WriteAsync(acceptRequest, cancellationToken).ConfigureAwait(false);
                return await acceptRequest.Task.ConfigureAwait(false);
            }
        }

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return new ValueTask<IConnectionListener>(Task.FromCanceled<IConnectionListener>(cancellationToken));
            return new ValueTask<IConnectionListener>(new InMemoryListener(this, endpoint));
        }

        private sealed class InMemoryListener : IConnectionListener
        {
            readonly CancellationTokenSource _cancellationTokenSource;
            readonly InMemoryListenerFactory _factory;
            public EndPoint EndPoint { get; }

            public InMemoryListener(InMemoryListenerFactory factory, EndPoint endPoint)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _factory = factory;
                EndPoint = endPoint;
            }

            public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
            {
                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return null;
                }

                using (CancellationTokenSource src = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token))
                {
                    try
                    {
                        return await _factory.AcceptAsync(src.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == src.Token && !cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }
                }
            }

            public ValueTask DisposeAsync()
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                return default;
            }

            public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
            {
                _cancellationTokenSource.Cancel();
                return default;
            }
        }

        private sealed class InMemoryConnectionContext
            : ConnectionContext
            , IFeatureCollection
            , IConnectionIdFeature
            , IConnectionTransportFeature
            , IConnectionItemsFeature
            , IMemoryPoolFeature
            , IConnectionLifetimeFeature
        {
            static int s_ids;

            readonly Dictionary<Type, object> _features = new Dictionary<Type, object>();
            int _featuresRevision = 0;

            IDictionary<object, object> _items;
            string _connectionId;

            public override string ConnectionId
            {
                get => _connectionId ??= Interlocked.Increment(ref s_ids).ToString(CultureInfo.InvariantCulture);
                set => _connectionId = value;
            }

            public override IFeatureCollection Features => this;

            public override IDictionary<object, object> Items
            {
                get => _items ??= new Dictionary<object, object>();
                set => _items = value;
            }

            public override IDuplexPipe Transport { get; set; }
            public DuplexPipeStream ClientTransport { get; }

            bool IFeatureCollection.IsReadOnly => false;

            int IFeatureCollection.Revision => _featuresRevision;

            public MemoryPool<byte> MemoryPool => null;

            object IFeatureCollection.this[Type key]
            {
                get
                {
                    if (_features.TryGetValue(key, out object instance))
                    {
                        return instance;
                    }

                    if (key.IsAssignableFrom(GetType()))
                    {
                        return this;
                    }

                    return null;
                }
                set
                {
                    _features[key] = value;
                    ++_featuresRevision;
                }
            }

            public InMemoryConnectionContext()
            {
                (Transport, ClientTransport) = DuplexPipeStream.CreateInMemoryPair();
            }

            TFeature IFeatureCollection.Get<TFeature>()
            {
                return (TFeature)((IFeatureCollection)this)[typeof(TFeature)];
            }

            void IFeatureCollection.Set<TFeature>(TFeature instance)
            {
                _features[typeof(TFeature)] = instance;
                ++_featuresRevision;
            }

            IEnumerator<KeyValuePair<Type, object>> IEnumerable<KeyValuePair<Type, object>>.GetEnumerator()
            {
                IFeatureCollection features = this;

                return _features.Keys
                    .Union(new[] { typeof(IConnectionIdFeature), typeof(IConnectionTransportFeature), typeof(IConnectionItemsFeature), typeof(IMemoryPoolFeature), typeof(IConnectionLifetimeFeature) })
                    .Select(type => new KeyValuePair<Type, object>(type, features[type]))
                    .GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return ((IEnumerable<KeyValuePair<Type, object>>)this).GetEnumerator();
            }
        }
    }
}
