﻿using BeatTogether.NativeUDP;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeatTogether.LiteNetLib
{
    /// <summary>
    /// UDP server is used to send or multicast datagrams to UDP endpoints
    /// </summary>
    /// <remarks>Thread-safe</remarks>
    public class UdpServer : IDisposable
    {
        private readonly ILogger _logger = Log.ForContext<UdpServer>();
        /// <summary>
        /// Initialize UDP server with a given IP address and port number
        /// </summary>
        /// <param name="address">IP address</param>
        /// <param name="port">Port number</param>
        public UdpServer(IPAddress address, int port) : this(new IPEndPoint(address, port)) { }
        /// <summary>
        /// Initialize UDP server with a given IP address and port number
        /// </summary>
        /// <param name="address">IP address</param>
        /// <param name="port">Port number</param>
        public UdpServer(string address, int port) : this(new IPEndPoint(IPAddress.Parse(address), port)) { }
        /// <summary>
        /// Initialize UDP server with a given IP endpoint
        /// </summary>
        /// <param name="endpoint">IP endpoint</param>
        public UdpServer(IPEndPoint endpoint)
        {
            Id = Guid.NewGuid();
            Endpoint = endpoint;
        }

        /// <summary>
        /// Server Id
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// IP endpoint
        /// </summary>
        public IPEndPoint Endpoint { get; private set; }
        /// <summary>
        /// Multicast IP endpoint
        /// </summary>
        public IPEndPoint MulticastEndpoint { get; private set; }
        /// <summary>
        /// Socket
        /// </summary>
        public Socket Socket { get; private set; }

        /// <summary>
        /// Number of bytes pending sent by the server
        /// </summary>
        public long BytesPending { get; private set; }
        /// <summary>
        /// Number of bytes sending by the server
        /// </summary>
        public long BytesSending { get; private set; }
        /// <summary>
        /// Number of bytes sent by the server
        /// </summary>
        public long BytesSent { get; private set; }
        /// <summary>
        /// Number of bytes received by the server
        /// </summary>
        public long BytesReceived { get; private set; }
        /// <summary>
        /// Number of datagrams sent by the server
        /// </summary>
        public long DatagramsSent { get; private set; }
        /// <summary>
        /// Number of datagrams received by the server
        /// </summary>
        public long DatagramsReceived { get; private set; }

        /// <summary>
        /// Option: dual mode socket
        /// </summary>
        /// <remarks>
        /// Specifies whether the Socket is a dual-mode socket used for both IPv4 and IPv6.
        /// Will work only if socket is bound on IPv6 address.
        /// </remarks>
        public bool OptionDualMode { get; set; }
        /// <summary>
        /// Option: reuse address
        /// </summary>
        /// <remarks>
        /// This option will enable/disable SO_REUSEADDR if the OS support this feature
        /// </remarks>
        public bool OptionReuseAddress { get; set; }
        /// <summary>
        /// Option: enables a socket to be bound for exclusive access
        /// </summary>
        /// <remarks>
        /// This option will enable/disable SO_EXCLUSIVEADDRUSE if the OS support this feature
        /// </remarks>
        public bool OptionExclusiveAddressUse { get; set; }
        /// <summary>
        /// Option: receive buffer size
        /// </summary>
        public int OptionReceiveBufferSize { get; set; } = 8192;
        /// <summary>
        /// Option: send buffer size
        /// </summary>
        public int OptionSendBufferSize { get; set; } = 8192;

        #region Connect/Disconnect client

        /// <summary>
        /// Is the server started?
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// Create a new socket object
        /// </summary>
        /// <remarks>
        /// Method may be override if you need to prepare some specific socket object in your implementation.
        /// </remarks>
        /// <returns>Socket object</returns>
        protected virtual Socket CreateSocket()
        {
            return new Socket(Endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        }

        /// <summary>
        /// Start the server (synchronous)
        /// </summary>
        /// <returns>'true' if the server was successfully started, 'false' if the server failed to start</returns>
        public virtual bool Start()
        {
            Debug.Assert(!IsStarted, "UDP server is already started!");
            if (IsStarted)
                return false;

            // Setup buffers
            _receiveBuffer = new Buffer();
            _sendBuffer = new Buffer();

            // Setup event args
            _receiveEventArg = new SocketAsyncEventArgs();
            _receiveEventArg.Completed += OnAsyncCompleted;
            _sendEventArg = new SocketAsyncEventArgs();
            _sendEventArg.Completed += OnAsyncCompleted;

            // Create a new server socket
            Socket = CreateSocket();

            // Update the server socket disposed flag
            IsSocketDisposed = false;

            // Apply the option: reuse address
            Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, OptionReuseAddress);
            // Apply the option: exclusive address use
            Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, OptionExclusiveAddressUse);
            // Apply the option: dual mode (this option must be applied before recieving)
            if (Socket.AddressFamily == AddressFamily.InterNetworkV6)
                Socket.DualMode = OptionDualMode;

            // Setup Socket
            //Socket.ReceiveTimeout = 10000;
            //Socket.ReceiveTimeout = 500;
            Socket.ReceiveTimeout = 5000;
            Socket.SendTimeout = 500;
            Socket.ReceiveBufferSize = OptionReceiveBufferSize;
            Socket.SendBufferSize = OptionSendBufferSize;

            // Bind the server socket to the IP endpoint
            Socket.Bind(Endpoint);
            // Refresh the endpoint property based on the actual endpoint created
            Endpoint = (IPEndPoint)Socket.LocalEndPoint!;

            // Prepare receive endpoint
            _receiveEndpoint = new IPEndPoint((Endpoint.AddressFamily == AddressFamily.InterNetworkV6) ? IPAddress.IPv6Any : IPAddress.Any, 0);

            // Prepare receive & send buffers
            _receiveBuffer.Reserve(OptionReceiveBufferSize);

            // Reset statistic
            BytesPending = 0;
            BytesSending = 0;
            BytesSent = 0;
            BytesReceived = 0;
            DatagramsSent = 0;
            DatagramsReceived = 0;

            // Update the started flag
            IsStarted = true;

            _threadv4 = new Thread(NativeReceive!)
            {
                Name = $"UdpSocketThreadv4({Endpoint.Port})",
                IsBackground = true
            };
            _threadv4.Start(Socket);

            // Call the server started handler
            OnStarted();

            return true;
        }

        /// <summary>
        /// Start the server with a given multicast IP address and port number (synchronous)
        /// </summary>
        /// <param name="multicastAddress">Multicast IP address</param>
        /// <param name="multicastPort">Multicast port number</param>
        /// <returns>'true' if the server was successfully started, 'false' if the server failed to start</returns>
        public virtual bool Start(IPAddress multicastAddress, int multicastPort) { return Start(new IPEndPoint(multicastAddress, multicastPort)); }

        /// <summary>
        /// Start the server with a given multicast IP address and port number (synchronous)
        /// </summary>
        /// <param name="multicastAddress">Multicast IP address</param>
        /// <param name="multicastPort">Multicast port number</param>
        /// <returns>'true' if the server was successfully started, 'false' if the server failed to start</returns>
        public virtual bool Start(string multicastAddress, int multicastPort) { return Start(new IPEndPoint(IPAddress.Parse(multicastAddress), multicastPort)); }

        /// <summary>
        /// Start the server with a given multicast endpoint (synchronous)
        /// </summary>
        /// <param name="multicastEndpoint">Multicast IP endpoint</param>
        /// <returns>'true' if the server was successfully started, 'false' if the server failed to start</returns>
        public virtual bool Start(IPEndPoint multicastEndpoint)
        {
            MulticastEndpoint = multicastEndpoint;
            return Start();
        }

        /// <summary>
        /// Stop the server (synchronous)
        /// </summary>
        /// <returns>'true' if the server was successfully stopped, 'false' if the server is already stopped</returns>
        public virtual bool Stop()
        {
            Debug.Assert(IsStarted, "UDP server is not started!");
            if (!IsStarted)
                return false;

            // Reset event args
            _receiveEventArg.Completed -= OnAsyncCompleted;
            _sendEventArg.Completed -= OnAsyncCompleted;

            try
            {
                // Close the server socket
                Socket.Close();

                // Dispose the server socket
                Socket.Dispose();

                // Dispose event arguments
                _receiveEventArg.Dispose();
                _sendEventArg.Dispose();

                // Update the server socket disposed flag
                IsSocketDisposed = false;
            }
            catch (ObjectDisposedException) { }

            // Update the started flag
            IsStarted = false;

            // Update sending/receiving flags
            //_receiving = false;
            _receivingSem.Dispose();
            _sending = false;

            // Clear send/receive buffers
            ClearBuffers();

            // Call the server stopped handler
            OnStopped();

            return true;
        }

        /// <summary>
        /// Restart the server (synchronous)
        /// </summary>
        /// <returns>'true' if the server was successfully restarted, 'false' if the server failed to restart</returns>
        public virtual bool Restart()
        {
            if (!Stop())
                return false;

            return Start();
        }

        #endregion

        #region Send/Recieve data

        // Receive and send endpoints
        EndPoint _receiveEndpoint;
        private readonly Dictionary<NativeAddr, IPEndPoint> _nativeAddrMap = new Dictionary<NativeAddr, IPEndPoint>();
        EndPoint _sendEndpoint;
        // Receive buffer
        private bool _receiving;
        private SemaphoreSlim _receivingSem = new(1);
        private Buffer _receiveBuffer;
        private SocketAsyncEventArgs _receiveEventArg;
        // Send buffer
        private bool _sending;
        private Buffer _sendBuffer;
        private SocketAsyncEventArgs _sendEventArg;
        [ThreadStatic] private static byte[] _endPointBuffer;
        // Threads
        private Thread _threadv4;

        /// <summary>
        /// Multicast datagram to the prepared mulicast endpoint (asynchronous)
        /// </summary>
        /// <param name="buffer">Datagram buffer to multicast</param>
        /// <param name="offset">Datagram buffer offset</param>
        /// <param name="size">Datagram buffer size</param>
        /// <returns>'true' if the datagram was successfully multicasted, 'false' if the datagram was not multicasted</returns>
        public virtual bool MulticastAsync(ReadOnlySpan<byte> buffer) { return SendAsync(MulticastEndpoint, buffer); }

        /// <summary>
        /// Multicast text to the prepared mulicast endpoint (asynchronous)
        /// </summary>
        /// <param name="text">Text string to multicast</param>
        /// <returns>'true' if the text was successfully multicasted, 'false' if the text was not multicasted</returns>
        public virtual bool MulticastAsync(string text) { return MulticastAsync(Encoding.UTF8.GetBytes(text)); }

        /// <summary>
        /// Send datagram to the given endpoint (asynchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to send</param>
        /// <param name="buffer">Datagram buffer to send</param>
        /// <param name="offset">Datagram buffer offset</param>
        /// <param name="size">Datagram buffer size</param>
        /// <returns>'true' if the datagram was successfully sent, 'false' if the datagram was not sent</returns>
        public virtual bool SendAsync(EndPoint endpoint, ReadOnlySpan<byte> buffer)
        {
            if (_sending)
                return false;

            if (!IsStarted)
                return false;

            if (buffer.Length == 0)
                return true;

            // Fill the main send buffer
            _sendBuffer.Append(buffer);

            // Update statistic
            BytesSending = _sendBuffer.Size;

            // Update send endpoint
            _sendEndpoint = endpoint;

            // Try to send the main buffer
            Task.Factory.StartNew(TrySend);

            return true;
        }

        /// <summary>
        /// Send text to the given endpoint (asynchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to send</param>
        /// <param name="text">Text string to send</param>
        /// <returns>'true' if the text was successfully sent, 'false' if the text was not sent</returns>
        public virtual bool SendAsync(EndPoint endpoint, string text) { return SendAsync(endpoint, Encoding.UTF8.GetBytes(text)); }

        /// <summary>
        /// Receive a new datagram from the given endpoint (synchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to receive from</param>
        /// <param name="buffer">Datagram buffer to receive</param>
        /// <returns>Size of received datagram</returns>
        public virtual long Receive(ref EndPoint endpoint, byte[] buffer) { return Receive(ref endpoint, buffer, 0, buffer.Length); }

        /// <summary>
        /// Receive a new datagram from the given endpoint (synchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to receive from</param>
        /// <param name="buffer">Datagram buffer to receive</param>
        /// <param name="offset">Datagram buffer offset</param>
        /// <param name="size">Datagram buffer size</param>
        /// <returns>Size of received datagram</returns>
        public virtual long Receive(ref EndPoint endpoint, byte[] buffer, long offset, long size)
        {
            if (!IsStarted)
                return 0;

            if (size == 0)
                return 0;

            try
            {
                // Receive datagram from the client
                int received = Socket.ReceiveFrom(buffer, (int)offset, (int)size, SocketFlags.None, ref endpoint);

                // Update statistic
                DatagramsReceived++;
                BytesReceived += received;

                // Call the datagram received handler
                OnReceived(endpoint, buffer.AsSpan((int)offset, (int)size));

                return received;
            }
            catch (ObjectDisposedException) { return 0; }
            catch (SocketException ex)
            {
                SendError(ex.SocketErrorCode);
                return 0;
            }
        }

        /// <summary>
        /// Receive text from the given endpoint (synchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to receive from</param>
        /// <param name="size">Text size to receive</param>
        /// <returns>Received text</returns>
        public virtual string Receive(ref EndPoint endpoint, long size)
        {
            var buffer = new byte[size];
            var length = Receive(ref endpoint, buffer);
            return Encoding.UTF8.GetString(buffer, 0, (int)length);
        }

        /// <summary>
        /// Receive datagram from the client (asynchronous)
        /// </summary>
        public virtual void ReceiveAsync()
        {
            // Try to receive datagram
            Task.Factory.StartNew(TryReceive);
        }

        private async void NativeReceive(object state)
        {
            if (_receivingSem.CurrentCount <= 0)
                return;

            if (!IsStarted)
                return;

            //try
            //{
                //_receiving = true;
                Socket socket = (Socket)state;
                while (IsStarted)
                {
                    await _receivingSem.WaitAsync();
                    //if (Socket.Available > 0)
                    //{
                        //IntPtr socketHandle = socket.Handle;
                        byte[] addrBuffer = new byte[NativeSocket.IPv4AddrSize];

                        int addrSize = addrBuffer.Length;
                        //Async receive with the receive handler
                        //Reading data

                        //Socket.ReceiveTimeout = 0;
                        //int size = Socket.ReceiveFrom(_receiveBuffer.Data, 0, (int)_receiveBuffer.Capacity, SocketFlags.None,
                        //    ref _receiveEndpoint);
                        //_logger.Debug($"ReceiveFrom EndPoint {_receiveEndpoint as IPEndPoint}");

                        int size = NativeSocket.RecvFrom(socket.Handle, _receiveBuffer.Data, (int)_receiveBuffer.Capacity, addrBuffer, ref addrSize);
                        ////string dataStr = "";
                        ////foreach(byte recvData in _receiveBuffer.Data)
                        ////{
                        ////    dataStr += recvData.ToString() + ";";
                        ////}
                        ////_logger.Verbose($"Received {size} data from endpoint {new NativeEndPoint(addrBuffer)} with buffer {dataStr}");
                        //_logger.Verbose($"Received {size} bytes data from endpoint {new NativeEndPoint(addrBuffer)}");
                        if (size == 0)
                            continue;
                        if (size == -1)
                        {
                            SocketError errorCode = NativeSocket.GetSocketError();
                            _logger.Verbose($"SocketError {errorCode}");

                            if (errorCode == SocketError.WouldBlock || errorCode == SocketError.TimedOut) //Linux timeout EAGAIN
                                //return;
                                continue;
                            if (ProcessError(new SocketException((int)errorCode)))
                                return;
                            //return;
                            continue;
                        }

                        NativeAddr nativeAddr = new NativeAddr(addrBuffer, addrSize);
                        if (!_nativeAddrMap.TryGetValue(nativeAddr, out var endPoint))
                            endPoint = new NativeEndPoint(addrBuffer);

                        //All ok!
                        //NetDebug.WriteForce($"[R]Received data from {endPoint}, result: {packet.Size}");
                        //OnMessageReceived(packet, endPoint);
                        //packet = PoolGetPacket(NetConstants.MaxPacketSize);
                        //_receiveEventArg.RemoteEndPoint = endPoint;
                        //_receiveEventArg.SetBuffer(_receiveBuffer.Data, 0, (int)_receiveBuffer.Capacity);
                        _receiveEndpoint = endPoint;
                        _receivingSem.Release();
                        OnReceived(_receiveEndpoint, _receiveBuffer.Data.AsSpan(0, (int)size));
                        //ProcessReceiveFrom(_receiveEventArg);
                        //}
                        //if (!Socket.ReceiveFromAsync(_receiveEventArg))
                    //}
                    //Thread.Yield();
                }
                //_receiving = false;
            //}
            //catch (ObjectDisposedException) { }
            //catch (SocketException ex)
            //{
            //    ProcessError(ex);
            //}
        }

        /// <summary>
        /// Try to receive new data
        /// </summary>
        private void TryReceive()
        {
            if (_receiving)
                return;

            if (!IsStarted)
                return;

            //try
            //{
                _receiving = true;
                while (IsStarted)
                {
                    //if (Socket.Available > 0)
                    //{
                        IntPtr socketHandle = Socket.Handle;
                        byte[] addrBuffer = new byte[Socket.AddressFamily == AddressFamily.InterNetwork
                            ? NativeSocket.IPv4AddrSize
                            : NativeSocket.IPv6AddrSize];

                        int addrSize = addrBuffer.Length;
                        //Async receive with the receive handler
                        //Reading data

                        //Socket.ReceiveTimeout = 0;
                        //int size = Socket.ReceiveFrom(_receiveBuffer.Data, 0, (int)_receiveBuffer.Capacity, SocketFlags.None,
                        //    ref _receiveEndpoint);
                        //_logger.Debug($"ReceiveFrom EndPoint {_receiveEndpoint as IPEndPoint}");

                        int size = NativeSocket.RecvFrom(socketHandle, _receiveBuffer.Data, (int)_receiveBuffer.Capacity, addrBuffer, ref addrSize);
                        ////string dataStr = "";
                        ////foreach(byte recvData in _receiveBuffer.Data)
                        ////{
                        ////    dataStr += recvData.ToString() + ";";
                        ////}
                        ////_logger.Verbose($"Received {size} data from endpoint {new NativeEndPoint(addrBuffer)} with buffer {dataStr}");
                        //_logger.Verbose($"Received {size} bytes data from endpoint {new NativeEndPoint(addrBuffer)}");
                        if (size == 0)
                            return;
                        if (size == -1)
                        {
                            SocketError errorCode = NativeSocket.GetSocketError();
                            _logger.Verbose($"SocketError {errorCode}");

                            if (errorCode == SocketError.WouldBlock || errorCode == SocketError.TimedOut) //Linux timeout EAGAIN
                                                                                                          //return;
                                continue;
                            if (ProcessError(new SocketException((int)errorCode)))
                                return;
                            //return;
                            continue;
                        }

                        NativeAddr nativeAddr = new NativeAddr(addrBuffer, addrSize);
                        if (!_nativeAddrMap.TryGetValue(nativeAddr, out var endPoint))
                            endPoint = new NativeEndPoint(addrBuffer);

                        //All ok!
                        //NetDebug.WriteForce($"[R]Received data from {endPoint}, result: {packet.Size}");
                        //OnMessageReceived(packet, endPoint);
                        //packet = PoolGetPacket(NetConstants.MaxPacketSize);
                        //_receiveEventArg.RemoteEndPoint = endPoint;
                        //_receiveEventArg.SetBuffer(_receiveBuffer.Data, 0, (int)_receiveBuffer.Capacity);
                        _receiveEndpoint = endPoint;
                        OnReceived(_receiveEndpoint, _receiveBuffer.Data.AsSpan(0, (int)size));
                        //ProcessReceiveFrom(_receiveEventArg);
                        //}
                        //if (!Socket.ReceiveFromAsync(_receiveEventArg))
                    //}
                    //Thread.Yield();
                }
                _receiving = false;
            //}
            //catch (ObjectDisposedException) { }
            //catch (SocketException ex)
            //{
            //    ProcessError(ex);
            //}
        }

        /// <summary>
        /// Try to send pending data
        /// </summary>
        private void TrySend()
        {
            if (_sending)
                return;

            if (!IsStarted)
                return;

            //try
            //{
                _sending = true;
                var remoteEndPoint = _sendEndpoint as IPEndPoint;
                // Async write with the write handler
                byte[] socketAddress;

                if (remoteEndPoint is NativeEndPoint nep)
                {
                    socketAddress = nep.NativeAddress;
                }
                else //Convert endpoint to raw
                {
                    if (_endPointBuffer == null)
                        _endPointBuffer = new byte[NativeSocket.IPv6AddrSize];
                    socketAddress = _endPointBuffer;

                    bool ipv4 = remoteEndPoint!.AddressFamily == AddressFamily.InterNetwork;
                    short addressFamily = NativeSocket.GetNativeAddressFamily(remoteEndPoint);

                    socketAddress[0] = (byte)(addressFamily);
                    socketAddress[1] = (byte)(addressFamily >> 8);
                    socketAddress[2] = (byte)(remoteEndPoint.Port >> 8);
                    socketAddress[3] = (byte)(remoteEndPoint.Port);

                    if (ipv4)
                    {
#pragma warning disable 618
                        long addr = remoteEndPoint.Address.Address;
#pragma warning restore 618
                        socketAddress[4] = (byte)(addr);
                        socketAddress[5] = (byte)(addr >> 8);
                        socketAddress[6] = (byte)(addr >> 16);
                        socketAddress[7] = (byte)(addr >> 24);
                    }
                    else
                    {
#if NETCOREAPP || NETSTANDARD2_1 || NETSTANDARD2_1_OR_GREATER
                        remoteEndPoint.Address.TryWriteBytes(new Span<byte>(socketAddress, 8, 16), out _);
#else
                            byte[] addrBytes = remoteEndPoint.Address.GetAddressBytes();
                            Buffer.BlockCopy(addrBytes, 0, socketAddress, 8, 16);
#endif
                    }
                }
                //Socket.SendBufferSize = (int)(_sendBuffer.Size);
                //_logger.Verbose($"Sending {_sendBuffer.Size} bytes data");
                //var result = Socket.SendTo(_sendBuffer.Data, 0, (int)(_sendBuffer.Size), SocketFlags.None, _sendEndpoint);
                //_logger.Verbose($"Sent {result} bytes data");
                var result = NativeSocket.SendTo(Socket.Handle, _sendBuffer.Data, (int)(_sendBuffer.Size), socketAddress, socketAddress.Length);
                if (result == -1)
                    throw NativeSocket.GetSocketException();
                if (result != (int)_sendBuffer.Size) _logger.Verbose($"Sent amount does not match buffer size, sent {result} bytes, buffer had {(int)_sendBuffer.Size} bytes");
                if (result > 0) _sendBuffer.Clear();
                //if (!Socket.SendToAsync(_sendEventArg))
                //_sendEventArg.RemoteEndPoint = _sendEndpoint;
                //_sendEventArg.SetBuffer(_sendBuffer.Data, 0, (int)(_sendBuffer.Size));

                //ProcessSendTo(_sendEventArg);
                _sending = false;
                //_logger.Verbose($"Sent {result}");
                OnSent(_sendEndpoint, result);
            //}
            //catch (SocketException ex)
            //{
            //    ProcessError(ex);
            //    SendError(ex.SocketErrorCode);
            //}
            //catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Clear send/receive buffers
        /// </summary>
        private void ClearBuffers()
        {
            // Clear send buffers
            _sendBuffer.Clear();

            // Update statistic
            BytesPending = 0;
            BytesSending = 0;
        }

        #endregion

        #region IO processing

        /// <summary>
        /// This method is called whenever a receive or send operation is completed on a socket
        /// </summary>
        private void OnAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            // Determine which type of operation just completed and call the associated handler
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.ReceiveFrom:
                    ProcessReceiveFrom(e);
                    break;
                case SocketAsyncOperation.SendTo:
                    ProcessSendTo(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }

        }

        /// <summary>
        /// This method is invoked when an asynchronous receive from operation completes
        /// </summary>
        private void ProcessReceiveFrom(SocketAsyncEventArgs e)
        {
            _receiving = false;

            if (!IsStarted)
                return;

            // Check for error
            if (e.SocketError != SocketError.Success)
            {
                SendError(e.SocketError);

                // Call the datagram received zero handler
                OnReceived(e.RemoteEndPoint!, ReadOnlySpan<byte>.Empty);

                return;
            }

            // Received some data from the client
            long size = e.BytesTransferred;

            // Update statistic
            DatagramsReceived++;
            BytesReceived += size;

            // Call the datagram received handler
            OnReceived(e.RemoteEndPoint!, _receiveBuffer.Data.AsSpan(0, (int)size));

            // If the receive buffer is full increase its size
            if (_receiveBuffer.Capacity == size)
                _receiveBuffer.Reserve(2 * size);
        }

        /// <summary>
        /// This method is invoked when an asynchronous send to operation completes
        /// </summary>
        private void ProcessSendTo(SocketAsyncEventArgs e)
        {
            _sending = false;

            if (!IsStarted)
                return;

            // Check for error
            if (e.SocketError != SocketError.Success)
            {
                SendError(e.SocketError);

                // Call the buffer sent zero handler
                OnSent(_sendEndpoint, 0);

                return;
            }

            long sent = e.BytesTransferred;

            // Send some data to the client
            if (sent > 0)
            {
                // Update statistic
                BytesSending = 0;
                BytesSent += sent;

                // Clear the send buffer
                _sendBuffer.Clear();

                // Call the buffer sent handler
                OnSent(_sendEndpoint, sent);
            }
        }

        #endregion

        #region Datagram handlers

        /// <summary>
        /// Handle server started notification
        /// </summary>
        protected virtual void OnStarted() { }
        /// <summary>
        /// Handle server stopped notification
        /// </summary>
        protected virtual void OnStopped() { }

        /// <summary>
        /// Handle datagram received notification
        /// </summary>
        /// <param name="endpoint">Received endpoint</param>
        /// <param name="buffer">Received datagram buffer</param>
        /// <remarks>
        /// Notification is called when another datagram was received from some endpoint
        /// </remarks>
        protected virtual void OnReceived(EndPoint endpoint, ReadOnlySpan<byte> buffer) { }
        /// <summary>
        /// Handle datagram sent notification
        /// </summary>
        /// <param name="endpoint">Endpoint of sent datagram</param>
        /// <param name="sent">Size of sent datagram buffer</param>
        /// <remarks>
        /// Notification is called when a datagram was sent to the client.
        /// This handler could be used to send another datagram to the client for instance when the pending size is zero.
        /// </remarks>
        protected virtual void OnSent(EndPoint endpoint, long sent) { }

        /// <summary>
        /// Handle error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        protected virtual void OnError(SocketError error) { }

        #endregion

        #region Error handling

        /// <summary>
        /// Send error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        private void SendError(SocketError error)
        {
            // Skip disconnect errors
            if ((error == SocketError.ConnectionAborted) ||
                (error == SocketError.ConnectionRefused) ||
                (error == SocketError.ConnectionReset) ||
                (error == SocketError.OperationAborted) ||
                (error == SocketError.Shutdown))
                return;

            OnError(error);
        }

        #endregion

        #region IDisposable implementation

        /// <summary>
        /// Disposed flag
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Server socket disposed flag
        /// </summary>
        public bool IsSocketDisposed { get; private set; } = true;

        // Implement IDisposable.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposingManagedResources)
        {
            // The idea here is that Dispose(Boolean) knows whether it is
            // being called to do explicit cleanup (the Boolean is true)
            // versus being called due to a garbage collection (the Boolean
            // is false). This distinction is useful because, when being
            // disposed explicitly, the Dispose(Boolean) method can safely
            // execute code using reference type fields that refer to other
            // objects knowing for sure that these other objects have not been
            // finalized or disposed of yet. When the Boolean is false,
            // the Dispose(Boolean) method should not execute code that
            // refer to reference type fields because those objects may
            // have already been finalized."

            if (!IsDisposed)
            {
                if (disposingManagedResources)
                {
                    // Dispose managed resources here...
                    Stop();
                }

                // Dispose unmanaged resources here...

                // Set large fields to null here...

                // Mark as disposed.
                IsDisposed = true;
            }
        }

        // Use C# destructor syntax for finalization code.
        ~UdpServer()
        {
            // Simply call Dispose(false).
            Dispose(false);
        }

        #endregion

        #region Helper functions

        private bool ProcessError(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.Interrupted:
                case SocketError.NotSocket:
                case SocketError.OperationAborted:
                    return true;
                case SocketError.ConnectionReset:
                case SocketError.MessageSize:
                case SocketError.TimedOut:
                case SocketError.NetworkReset:
                    _logger.Debug($"[R]Ignored error: {(int)ex.SocketErrorCode} - {ex}");
                    //NetDebug.Write($"[R]Ignored error: {(int)ex.SocketErrorCode} - {ex}");
                    break;
                default:
                    _logger.Error($"[R]Error code: {(int)ex.SocketErrorCode} - {ex}");
                    //NetDebug.WriteError($"[R]Error code: {(int)ex.SocketErrorCode} - {ex}");
                    //CreateEvent(NetEvent.EType.Error, errorCode: ex.SocketErrorCode);
                    break;
            }
            return false;
        }

        #endregion
    }
}
