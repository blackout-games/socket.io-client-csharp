using Newtonsoft.Json;
using SocketIOClient.EventArguments;
using SocketIOClient.Exceptions;
using SocketIOClient.JsonConverters;
using SocketIOClient.Packgers;
using SocketIOClient.Response;
using SocketIOClient.Util;
using SocketIOClient.WebSocketClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketIOClient
{
    /// <summary>
    /// socket.io client class
    /// </summary>
    public class SocketIO
    {
        /// <summary>
        /// Create SocketIO object with default options
        /// </summary>
        /// <param name="uri"></param>
        public SocketIO(string uri) : this(new Uri(uri)) { }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        public SocketIO(Uri uri) : this(uri, new SocketIOOptions()) { }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="options"></param>
        public SocketIO(string uri, SocketIOOptions options) : this(new Uri(uri), options) { }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="options"></param>
        public SocketIO(Uri uri, SocketIOOptions options)
        {
            ServerUri = uri;
            Options = options;
            Initialize();
        }

        /// <summary>
        /// Create SocketIO object with default options
        /// </summary>
        public SocketIO()
        {
            Options = new SocketIOOptions();
            Initialize();
        }

        Uri _serverUri;
        public Uri ServerUri
        {
            get => _serverUri;
            set
            {
                if (_serverUri != value)
                {
                    _serverUri = value;
                    if (value != null && value.AbsolutePath != "/")
                    {
                        Namespace = value.AbsolutePath + ',';
                    }
                }
            }
        }

        public UrlConverter UrlConverter { get; set; }

        public IWebSocketClient Socket { get; set; }

        /// <summary>
        /// An unique identifier for the socket session. Set after the connect event is triggered, and updated after the reconnect event.
        /// </summary>
        public string Id { get; set; }

        public string Namespace { get; private set; }

        /// <summary>
        /// Whether or not the socket is connected to the server.
        /// </summary>
        public bool Connected { get; private set; }

        /// <summary>
        /// Whether or not the socket is disconnected from the server.
        /// </summary>
        public bool Disconnected { get; private set; }
        internal int PacketId { get; private set; }
        internal List<byte[]> OutGoingBytes { get; private set; }
        internal Dictionary<int, Action<SocketIOResponse>> Acks { get; private set; }
        internal DateTime PingTime { get; set; }

        public SocketIOOptions Options { get; }
        ByteArrayConverter _byteArrayConverter;

        internal Dictionary<string, Action<SocketIOResponse>> Handlers { get; set; }

        CancellationTokenSource _pingToken;
        static readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);

        #region Socket.IO event
        private event EventHandler _onConnected = (x, y) => { };
        public event EventHandler OnConnected
        {
            add { _onConnected += value; }
            remove { _onConnected -= value; }
        }
        
        //private event EventHandler<string> OnConnectError;
        //private event EventHandler<string> OnConnectTimeout;
        private event EventHandler<string> _onError = (x, y) => { };
        public event EventHandler<string> OnError
        {
            add => _onError += value;
            remove => _onError -= value;
        }
        
        private event EventHandler<string> _onDisconnected = (x, y) => { };
        public event EventHandler<string> OnDisconnected
        {
            add => _onDisconnected += value;
            remove => _onDisconnected -= value;
        }
        
        //private event EventHandler<string> OnReconnectAttempt;
        private event EventHandler<int> _onReconnecting = (x, y) => { };
        public event EventHandler<int> OnReconnecting
        {
            add => _onReconnecting += value;
            remove => _onReconnecting -= value;
        }
        
        //private event EventHandler<string> OnReconnectError;
        private event EventHandler<Exception> _onReconnectFailed = (x, y) => { };
        public event EventHandler<Exception> OnReconnectFailed
        {
            add => _onReconnectFailed += value;
            remove => _onReconnectFailed -= value;
        }
        
        private event EventHandler _onPing = (x, y) => { };
        public event EventHandler OnPing
        {
            add => _onPing += value;
            remove => _onPing -= value;
        }
        
        private event EventHandler<TimeSpan> _onPong = (x, y) => { };
        public event EventHandler<TimeSpan> OnPong
        {
            add => _onPong += value;
            remove => _onPong -= value;
        }
        
        private event EventHandler<ReceivedEventArgs> _onReceivedEvent = (x, y) => { };
        public event EventHandler<ReceivedEventArgs> OnReceivedEvent
        {
            add => _onReceivedEvent += value;
            remove => _onReceivedEvent -= value;
        }

        private event EventHandler<byte[]> _onBytesReceived = (x, y) => { };
        internal event EventHandler<byte[]> OnBytesReceived
        {
            add => _onBytesReceived += value;
            remove => _onBytesReceived -= value;
        }
        
        #endregion

        private void Initialize()
        {
            UrlConverter = new UrlConverter();

#if NET45
            if (SystemUtil.IsWindows7)
                Socket = WebSocketClientFactory.CreateWebSocketSharpClient(this);
#endif
            if (Socket == null)
                Socket = WebSocketClientFactory.CreateClientWebSocket(this);

            PacketId = -1;
            Acks = new Dictionary<int, Action<SocketIOResponse>>();
            Handlers = new Dictionary<string, Action<SocketIOResponse>>();
            OutGoingBytes = new List<byte[]>();
            _byteArrayConverter = new ByteArrayConverter
            {
                Client = this
            };
            Disconnected = true;
            _onDisconnected += SocketIO_OnDisconnected;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task ConnectAsync()
        {
            await ConnectCoreAsync(Options.AllowedRetryFirstConnection);
        }

        private async Task ConnectCoreAsync(bool allowedRetryFirstConnection)
        {
            if (ServerUri == null)
            {
                throw new ArgumentException("Invalid ServerUri");
            }
            Uri wsUri = UrlConverter.HttpToWs(ServerUri, Options);
            if (allowedRetryFirstConnection)
            {
                double delayDouble = Options.ReconnectionDelay;
                while (true)
                {
                    try
                    {
                        await Socket.ConnectAsync(wsUri);
                        break;
                    }
                    catch (SystemException ex)
                    {
                        if (ex is TimeoutException || ex is System.Net.WebSockets.WebSocketException)
                        {
                            int delay = (int)delayDouble;
                            await Task.Delay(delay);
                            delayDouble += 2 * Options.RandomizationFactor;
                            if (delayDouble > Options.ReconnectionDelayMax)
                            {
                                _onReconnectFailed?.Invoke(this, ex);
                                break;
                            }
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }
            else
            {
                await Socket.ConnectAsync(wsUri);
            }
        }

        public async Task DisconnectAsync()
        {
            if (Connected && !Disconnected)
            {
                try
                {
                    await Socket.SendMessageAsync("41" + Namespace);
                }
                catch (Exception ex) { Trace.WriteLine(ex.Message); }
                Connected = false;
                Disconnected = true;
                try
                {
                    await Socket.DisconnectAsync();
                }
                catch (Exception ex) { Trace.WriteLine(ex.Message); }
                _pingToken.Cancel();
            }
        }

        /// <summary>
        /// Register a new handler for the given event.
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="callback"></param>
        public void On(string eventName, Action<SocketIOResponse> callback)
        {
            if (Handlers.ContainsKey(eventName))
            {
                Handlers.Remove(eventName);
            }
            Handlers.Add(eventName, callback);
        }

        /// <summary>
        /// Unregister a new handler for the given event.
        /// </summary>
        /// <param name="eventName"></param>
        public void Off(string eventName)
        {
            if (Handlers.ContainsKey(eventName))
            {
                Handlers.Remove(eventName);
            }
        }

        private async Task EmityCoreAsync(string eventName, int packetId, string data)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("Event name is invalid");
            }
            var builder = new StringBuilder();
            if (OutGoingBytes.Count > 0)
                builder.Append("45").Append(OutGoingBytes.Count).Append("-");
            else
                builder.Append("42");
            builder.Append(Namespace);
            if (packetId > -1)
            {
                builder.Append(packetId);
            }
            builder.Append("[\"").Append(eventName).Append("\"");
            if (!string.IsNullOrEmpty(data))
            {
                builder.Append(',').Append(data);
            }
            builder.Append(']');
            string message = builder.ToString();
            await Socket.SendMessageAsync(message);
            if (OutGoingBytes.Count > 0)
            {
                foreach (var item in OutGoingBytes)
                {
                    await Socket.SendMessageAsync(item);
                }
                OutGoingBytes.Clear();
            }
        }

        internal async Task EmitCallbackAsync(int packetId, params object[] data)
        {
            string dataString = GetDataString(data);

            var builder = new StringBuilder();
            if (OutGoingBytes.Count > 0)
                builder.Append("46").Append(OutGoingBytes.Count).Append("-");
            else
                builder.Append("43");
            builder
                .Append(Namespace)
                .Append(packetId)
                .Append("[")
                .Append(dataString)
                .Append("]");
            string message = builder.ToString();
            await Socket.SendMessageAsync(message);
            if (OutGoingBytes.Count > 0)
            {
                foreach (var item in OutGoingBytes)
                {
                    await Socket.SendMessageAsync(item);
                }
                OutGoingBytes.Clear();
            }
        }

        private string GetDataString(params object[] data)
        {
            var builder = new StringBuilder();
            if (data != null && data.Length > 0)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    builder.Append(JsonConvert.SerializeObject(data[i], _byteArrayConverter));
                    if (i != data.Length - 1)
                    {
                        builder.Append(",");
                    }
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Emits an event to the socket
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="data">Any other parameters can be included. All serializable datastructures are supported, including byte[]</param>
        /// <returns></returns>
        public async Task EmitAsync(string eventName, params object[] data)
        {
            await _semaphoreSlim.WaitAsync();
            try
            {
                string dataString = GetDataString(data);
                await EmityCoreAsync(eventName, -1, dataString);
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        /// <summary>
        /// Emits an event to the socket
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="ack">will be called with the server answer.</param>
        /// <param name="data">Any other parameters can be included. All serializable datastructures are supported, including byte[]</param>
        /// <returns></returns>
        public async Task EmitAsync(string eventName, Action<SocketIOResponse> ack, params object[] data)
        {
            await _semaphoreSlim.WaitAsync();
            try
            {
                Acks.Add(++PacketId, ack);
                string dataString = GetDataString(data);
                await EmityCoreAsync(eventName, PacketId, dataString);
            }
            finally
            {
                _semaphoreSlim.Release();

            }
        }

        private async Task SendNamespaceAsync()
        {
            if (!string.IsNullOrEmpty(Namespace))
            {
                var builder = new StringBuilder();
                builder.Append("40");

                if (!string.IsNullOrEmpty(Namespace))
                {
                    builder.Append(Namespace.TrimEnd(','));
                }
                if (Options.Query != null && Options.Query.Count > 0)
                {
                    builder.Append('?');
                    int index = -1;
                    foreach (var item in Options.Query)
                    {
                        index++;
                        builder
                            .Append(item.Key)
                            .Append('=')
                            .Append(item.Value);
                        if (index < Options.Query.Count - 1)
                        {
                            builder.Append('&');
                        }
                    }
                }
                if (!string.IsNullOrEmpty(Namespace))
                {
                    builder.Append(',');
                }
                await Socket.SendMessageAsync(builder.ToString());
            }
        }

        internal void Open(OpenResponse openResponse)
        {
            Id = openResponse.Sid;
            _pingToken = new CancellationTokenSource();
            Task.Factory.StartNew(async () =>
            {
                await SendNamespaceAsync();
                while (true)
                {
                    await Task.Delay(openResponse.PingInterval);
                    if (_pingToken.IsCancellationRequested)
                        return;
                    try
                    {
                        PingTime = DateTime.Now;
                        await Socket.SendMessageAsync("2");
                        _onPing?.Invoke(this, new EventArgs());
                    }
                    catch (Exception ex) { Trace.TraceError(ex.ToString()); }
                }
            }, _pingToken.Token);
        }

        internal void InvokeConnect()
        {
            Connected = true;
            Disconnected = false;
            _onConnected?.Invoke(this, new EventArgs());
        }

        internal void InvokeBytesReceived(byte[] bytes)
        {
            _onBytesReceived?.Invoke(this, bytes);
        }

        internal void InvokeDisconnect(string reason)
        {
            if (Connected)
            {
                Connected = false;
                Disconnected = true;
                _onDisconnected?.Invoke(this, reason);
                _pingToken.Cancel();
            }
        }

        internal void InvokeError(string error)
        {
            _onError?.Invoke(this, error);
        }

        private async void SocketIO_OnDisconnected(object sender, string e)
        {
            if (Options.Reconnection)
            {
                double delayDouble = Options.ReconnectionDelay;
                int attempt = 0;
                while (true)
                {
                    int delay = (int)delayDouble;
                    Trace.WriteLine($"{DateTime.Now} Reconnection wait {delay} ms");
                    await Task.Delay(delay);
                    Trace.WriteLine($"{DateTime.Now} Delay done");
                    try
                    {
                        if (!Connected && Disconnected)
                        {
                            _onReconnecting?.Invoke(this, ++attempt);
                            await ConnectCoreAsync(false);
                        }
                        break;
                    }
                    catch (SystemException ex)
                    {
                        if (ex is TimeoutException || ex is System.Net.WebSockets.WebSocketException)
                        {
                            delayDouble += 2 * Options.RandomizationFactor;
                            if (delayDouble > Options.ReconnectionDelayMax)
                            {
                                _onReconnectFailed?.Invoke(this, ex);
                            }
                        }
                    }
                }
            }
        }

        internal void InvokePong(TimeSpan ms)
        {
            _onPong?.Invoke(this, ms);
        }

        internal void InvokeReceivedEvent(ReceivedEventArgs args)
        {
            _onReceivedEvent?.Invoke(this, args);
        }
    }
}