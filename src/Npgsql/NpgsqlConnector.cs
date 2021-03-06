//    Copyright (C) 2002 The Npgsql Development Team
//    npgsql-general@gborg.postgresql.org
//    http://gborg.postgresql.org/project/npgsql/projdisplay.php
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Npgsql.BackendMessages;
using Npgsql.FrontendMessages;
using Npgsql.TypeHandlers;
using NpgsqlTypes;
using Npgsql.Logging;

namespace Npgsql
{
    /// <summary>
    /// Represents a connection to a PostgreSQL backend. Unlike NpgsqlConnection objects, which are
    /// exposed to users, connectors are internal to Npgsql and are recycled by the connection pool.
    /// </summary>
    internal partial class NpgsqlConnector
    {
        readonly NpgsqlConnectionStringBuilder _settings;

        /// <summary>
        /// The physical connection socket to the backend.
        /// </summary>
        internal Socket Socket { get; set; }

        /// <summary>
        /// The physical connection stream to the backend, without anything on top.
        /// </summary>
        internal NetworkStream BaseStream { get; set; }

        /// <summary>
        /// The physical connection stream to the backend, layered with an SSL/TLS stream if in secure mode.
        /// </summary>
        internal Stream Stream { get; set; }

        /// <summary>
        /// Buffer used for reading data.
        /// </summary>
        internal NpgsqlBuffer Buffer { get; private set; }

        /// <summary>
        /// Version of backend server this connector is connected to.
        /// </summary>
        internal Version ServerVersion { get; set; }

        /// <summary>
        /// The secret key of the backend for this connector, used for query cancellation.
        /// </summary>
        internal int BackendSecretKey { get; private set; }

        /// <summary>
        /// The process ID of the backend for this connector.
        /// </summary>
        internal int BackendProcessId { get; private set; }

        /// <summary>
        /// A unique ID identifying this connector, used for logging. Currently mapped to BackendProcessId
        /// </summary>
        internal int Id { get { return BackendProcessId; } }

        internal TypeHandlerRegistry TypeHandlerRegistry { get; set; }

        /// <summary>
        /// The current transaction status for this connector.
        /// </summary>
        internal TransactionStatus TransactionStatus { get; set; }

        /// <summary>
        /// The transaction currently in progress, if any.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Note that this doesn't mean a transaction request has actually been sent to the backend - for
        /// efficiency we defer sending the request to the first query after BeginTransaction is called.
        /// See <see cref="TransactionStatus"/> for the actual transaction status.
        /// </para>
        /// <para>
        /// Also, the user can initiate a transaction in SQL (i.e. BEGIN), in which case there will be no
        /// NpgsqlTransaction instance. As a result, never check <see cref="Transaction"/> to know whether
        /// a transaction is in progress, check <see cref="TransactionStatus"/> instead.
        /// </para>
        /// </remarks>
        internal NpgsqlTransaction Transaction { private get; set; }

        /// <summary>
        /// The NpgsqlConnection that (currently) owns this connector. Null if the connector isn't
        /// owned (i.e. idle in the pool)
        /// </summary>
        internal NpgsqlConnection Connection { get; set; }

        /// <summary>
        /// The number of messages that were prepended to the current message chain, but not yet sent.
        /// Note that this only tracks messages which produce a ReadyForQuery message
        /// </summary>
        byte _pendingRfqPrependedMessages;

        /// <summary>
        /// The number of messages that were prepended and sent to the last message chain.
        /// Note that this only tracks messages which produce a ReadyForQuery message
        /// </summary>
        byte _sentRfqPrependedMessages;

        /// <summary>
        /// A chain of messages to be sent to the backend.
        /// </summary>
        readonly List<FrontendMessage> _messagesToSend;

        internal NpgsqlDataReader CurrentReader;

        /// <summary>
        /// Holds all run-time parameters received from the backend (via ParameterStatus messages)
        /// </summary>
        internal Dictionary<string, string> BackendParams;

#if !DNXCORE50
        internal SSPIHandler SSPI { get; set; }
#endif

        static readonly NpgsqlLogger Log = NpgsqlLogManager.GetCurrentClassLogger();

        SemaphoreSlim _notificationSemaphore;
        static readonly byte[] EmptyBuffer = new byte[0];
        int _notificationBlockRecursionDepth;

        #region Reusable Message Objects

        // Frontend. Note that these are only used for single-query commands.
        internal readonly ParseMessage    ParseMessage    = new ParseMessage();
        internal readonly BindMessage     BindMessage     = new BindMessage();
        internal readonly DescribeMessage DescribeMessage = new DescribeMessage();
        internal readonly ExecuteMessage  ExecuteMessage  = new ExecuteMessage();

        // Backend
        readonly CommandCompleteMessage      _commandCompleteMessage      = new CommandCompleteMessage();
        readonly ReadyForQueryMessage        _readyForQueryMessage        = new ReadyForQueryMessage();
        readonly ParameterDescriptionMessage _parameterDescriptionMessage = new ParameterDescriptionMessage();
        readonly DataRowSequentialMessage    _dataRowSequentialMessage    = new DataRowSequentialMessage();
        readonly DataRowNonSequentialMessage _dataRowNonSequentialMessage = new DataRowNonSequentialMessage();

        // Since COPY is rarely used, allocate these lazily
        CopyInResponseMessage  _copyInResponseMessage;
        CopyOutResponseMessage _copyOutResponseMessage;
        CopyDataMessage        _copyDataMessage;

        #endregion

        internal NpgsqlConnector(NpgsqlConnection connection)
            : this(connection.CopyConnectionStringBuilder())
        {
            Connection = connection;
            Connection.Connector = this;
        }

        /// <summary>
        /// Creates a new connector with the given connection string.
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        NpgsqlConnector(NpgsqlConnectionStringBuilder connectionString)
        {
            State = ConnectorState.Closed;
            TransactionStatus = TransactionStatus.Idle;
            _settings = connectionString;
            BackendParams = new Dictionary<string, string>();
            _messagesToSend = new List<FrontendMessage>();
            _preparedStatementIndex = 0;
            _portalIndex = 0;
        }

        #region Configuration settings

        /// <summary>
        /// Return Connection String.
        /// </summary>
        internal static bool UseSslStream = false;
        internal string ConnectionString { get { return _settings.ConnectionString; } }
        internal string Host { get { return _settings.Host; } }
        internal int Port { get { return _settings.Port; } }
        internal string Database { get { return _settings.ContainsKey(Keywords.Database) ? _settings.Database : _settings.UserName; } }
        internal string UserName { get { return _settings.UserName; } }
        internal string Password { get { return _settings.Password; } }
        internal string Krbsrvname { get { return _settings.Krbsrvname; } }
        internal bool SSL { get { return _settings.SSL; } }
        internal SslMode SslMode { get { return _settings.SslMode; } }
        internal int BufferSize { get { return _settings.BufferSize; } }
        internal int ConnectionTimeout { get { return _settings.Timeout; } }
        internal int DefaultCommandTimeout { get { return _settings.CommandTimeout; } }
        internal bool Enlist { get { return _settings.Enlist; } }
        internal bool IntegratedSecurity { get { return _settings.IntegratedSecurity; } }

        #endregion Configuration settings

        #region State management

        volatile int _state;

        /// <summary>
        /// Gets the current state of the connector
        /// </summary>
        internal ConnectorState State
        {
            get { return (ConnectorState)_state; }
            set
            {
                var newState = (int) value;
                if (newState == _state)
                    return;
                Interlocked.Exchange(ref _state, newState);

                if (value == ConnectorState.Ready)
                {
                    if (CurrentReader != null) {
                        CurrentReader.Command.State = CommandState.Idle;
                        CurrentReader = null;
                    }
                }
            }
        }

        /// <summary>
        /// Returns whether the connector is open, regardless of any task it is currently performing
        /// </summary>
        internal bool IsConnected
        {
            get
            {
                switch (State)
                {
                    case ConnectorState.Ready:
                    case ConnectorState.Executing:
                    case ConnectorState.Fetching:
                    case ConnectorState.Copy:
                        return true;
                    case ConnectorState.Closed:
                    case ConnectorState.Connecting:
                    case ConnectorState.Broken:
                        return false;
                    default:
                        throw new ArgumentOutOfRangeException("State", "Unknown state: " + State);
                }
            }
        }

        /// <summary>
        /// Returns whether the connector is open and performing a task, i.e. not ready for a query
        /// </summary>
        internal bool IsBusy
        {
            get
            {
                switch (State)
                {
                    case ConnectorState.Executing:
                    case ConnectorState.Fetching:
                    case ConnectorState.Copy:
                        return true;
                    case ConnectorState.Ready:
                    case ConnectorState.Closed:
                    case ConnectorState.Connecting:
                    case ConnectorState.Broken:
                        return false;
                    default:
                        throw new ArgumentOutOfRangeException("State", "Unknown state: " + State);
                }
            }
        }

        internal bool IsReady  { get { return State == ConnectorState.Ready;  } }
        internal bool IsClosed { get { return State == ConnectorState.Closed; } }
        internal bool IsBroken { get { return State == ConnectorState.Broken; } }

        internal void CheckReadyState()
        {
            switch (State)
            {
                case ConnectorState.Ready:
                    return;
                case ConnectorState.Closed:
                case ConnectorState.Broken:
                case ConnectorState.Connecting:
                    throw new InvalidOperationException("The Connection is not open.");
                case ConnectorState.Executing:
                case ConnectorState.Fetching:
                    throw new InvalidOperationException("There is already an open DataReader associated with this Connection which must be closed first.");
                case ConnectorState.Copy:
                    throw new InvalidOperationException("A COPY operation is in progress and must complete first.");
                default:
                    throw new ArgumentOutOfRangeException("Connector.State", "Unknown state: " + State);
            }
        }

        #endregion

        #region Open

        /// <summary>
        /// Opens the physical connection to the server.
        /// </summary>
        /// <remarks>Usually called by the RequestConnector
        /// Method of the connection pool manager.</remarks>
        internal void Open()
        {
            Contract.Requires(Connection != null && Connection.Connector == this);
            Contract.Ensures(State == ConnectorState.Ready);
            Contract.EnsuresOnThrow<IOException>(State == ConnectorState.Closed);
            Contract.EnsuresOnThrow<SocketException>(State == ConnectorState.Closed);

            if (State != ConnectorState.Closed) {
                throw new InvalidOperationException("Can't open, state is " + State);
            }

            State = ConnectorState.Connecting;

            ServerVersion = null;

            // Keep track of time remaining; Even though there may be multiple timeout-able calls,
            // this allows us to still respect the caller's timeout expectation.
            var connectTimeRemaining = ConnectionTimeout * 1000;

            try {
                // Get a raw connection, possibly SSL...
                RawOpen(connectTimeRemaining);

                var startupMessage = new StartupMessage(Database, UserName);
                if (!string.IsNullOrEmpty(_settings.ApplicationName)) {
                    startupMessage["application_name"] = _settings.ApplicationName;
                }
                if (!string.IsNullOrEmpty(_settings.SearchPath)) {
                    startupMessage["search_path"] = _settings.SearchPath;
                }
                if (!IsRedshift) {
                    startupMessage["ssl_renegotiation_limit"] = "0";
                }

                if (startupMessage.Length > Buffer.Size) {  // Should really never happen, just in case
                    throw new Exception("Startup message bigger than buffer");
                }
                startupMessage.Write(Buffer);

                Buffer.Flush();
                HandleAuthentication();

                ProcessServerVersion();
                TypeHandlerRegistry.Setup(this);
                State = ConnectorState.Ready;

                if (_settings.SyncNotification) {
                    AddNotificationListener();
                }
            }
            catch
            {
                try { Close(); }
                catch {
                    // ignored
                }
                throw;
            }
        }

        public void RawOpen(int timeout)
        {
            try
            {
                // Keep track of time remaining; Even though there may be multiple timeout-able calls,
                // this allows us to still respect the caller's timeout expectation.
                var attemptStart = DateTime.Now;
                var result = Dns.BeginGetHostAddresses(Host, null, null);

                if (!result.AsyncWaitHandle.WaitOne(timeout, true))
                {
                    // Timeout was used up attempting the Dns lookup
                    throw new TimeoutException("Dns hostname lookup timeout. Increase Timeout value in ConnectionString.");
                }

                timeout -= Convert.ToInt32((DateTime.Now - attemptStart).TotalMilliseconds);

                var ips = Dns.EndGetHostAddresses(result);

                // try every ip address of the given hostname, use the first reachable one
                // make sure not to exceed the caller's timeout expectation by splitting the
                // time we have left between all the remaining ip's in the list.
                for (var i = 0; i < ips.Length; i++)
                {
                    Log.Trace("Attempting to connect to " + ips[i], Id);
                    var ep = new IPEndPoint(ips[i], Port);
                    Socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    attemptStart = DateTime.Now;

                    try
                    {
                        result = Socket.BeginConnect(ep, null, null);

                        if (!result.AsyncWaitHandle.WaitOne(timeout / (ips.Length - i), true)) {
                            throw new TimeoutException("Connection establishment timeout. Increase Timeout value in ConnectionString.");
                        }

                        Socket.EndConnect(result);

                        // connect was successful, leave the loop
                        break;
                    }
                    catch (TimeoutException)
                    {
                        throw;
                    }
                    catch
                    {
                        Log.Warn("Failed to connect to " + ips[i]);
                        timeout -= Convert.ToInt32((DateTime.Now - attemptStart).TotalMilliseconds);

                        if (i == ips.Length - 1) {
                            throw;
                        }
                    }
                }

                Contract.Assert(Socket != null);
                BaseStream = new NetworkStream(Socket, true);
                Stream = BaseStream;

                // If the PostgreSQL server has SSL connectors enabled Open SslClientStream if (response == 'S') {
                if (SSL || (SslMode == SslMode.Require) || (SslMode == SslMode.Prefer))
                {
                    Stream
                        .WriteInt32(8)
                        .WriteInt32(80877103);

                    // Receive response
                    var response = (Char)Stream.ReadByte();

                    if (response != 'S')
                    {
                        if (SslMode == SslMode.Require) {
                            throw new InvalidOperationException("Ssl connection requested. No Ssl enabled connection from this host is configured.");
                        }
                    }
                    else
                    {
                        //create empty collection
                        var clientCertificates = new X509CertificateCollection();

                        //trigger the callback to fetch some certificates
                        DefaultProvideClientCertificatesCallback(clientCertificates);

                        if (!UseSslStream)
                        {
#if DNXCORE50
                            throw new NotSupportedException("TLS implementation not yet supported with .NET Core");
#else
                            var sslStream = new TlsClientStream.TlsClientStream(Stream);
                            sslStream.PerformInitialHandshake(Host, clientCertificates, DefaultValidateRemoteCertificateCallback, false);
                            Stream = sslStream;
#endif
                        }
                        else
                        {
                            var sslStream = new SslStream(Stream, false, DefaultValidateRemoteCertificateCallback);
                            sslStream.AuthenticateAsClient(Host, clientCertificates, System.Security.Authentication.SslProtocols.Default, false);
                            Stream = sslStream;
                        }
                        IsSecure = true;
                    }
                }

                Buffer = new NpgsqlBuffer(Stream, BufferSize, PGUtil.UTF8Encoding);
                Log.Debug(String.Format("Connected to {0}:{1}", Host, Port));
            }
            catch
            {
                if (Stream != null)
                {
                    try { Stream.Close(); } catch {
                        // ignored
                    }
                    Stream = null;
                }
                if (BaseStream != null)
                {
                    try { BaseStream.Close(); } catch {
                        // ignored
                    }
                    BaseStream = null;
                }
                if (Socket != null)
                {
                    try { Socket.Close(); } catch {
                        // ignored
                    }
                    Socket = null;
                }
                throw;
            }
        }

        void HandleAuthentication()
        {
            Log.Debug("Authenticating...", Id);
            while (true)
            {
                var msg = ReadSingleMessage();
                switch (msg.Code)
                {
                    case BackendMessageCode.ReadyForQuery:
                        State = ConnectorState.Ready;
                        return;
                    case BackendMessageCode.AuthenticationRequest:
                        ProcessAuthenticationMessage((AuthenticationRequestMessage)msg);
                        continue;
                    default:
                        throw new Exception("Unexpected message received while authenticating: " + msg.Code);
                }
            }
        }

        void ProcessAuthenticationMessage(AuthenticationRequestMessage msg)
        {
            PasswordMessage passwordMessage;

            switch (msg.AuthRequestType)
            {
                case AuthenticationRequestType.AuthenticationOk:
                    return;

                case AuthenticationRequestType.AuthenticationCleartextPassword:
                    passwordMessage = PasswordMessage.CreateClearText(Password);
                    break;

                case AuthenticationRequestType.AuthenticationMD5Password:
                    passwordMessage = PasswordMessage.CreateMD5(Password, UserName, ((AuthenticationMD5PasswordMessage)msg).Salt);
                    break;

                case AuthenticationRequestType.AuthenticationGSS:
                    if (!IntegratedSecurity) {
                        throw new Exception("GSS authentication but IntegratedSecurity not enabled");
                    }
#if DNXCORE50
                    throw new NotSupportedException("SSPI not yet supported in .NET Core");
#else
                    // For GSSAPI we have to use the supplied hostname
                    SSPI = new SSPIHandler(Host, Krbsrvname, true);
                    passwordMessage = new PasswordMessage(SSPI.Continue(null));
                    break;
#endif

                case AuthenticationRequestType.AuthenticationSSPI:
                    if (!IntegratedSecurity) {
                        throw new Exception("SSPI authentication but IntegratedSecurity not enabled");
                    }
#if DNXCORE50
                    throw new NotSupportedException("SSPI not yet supported in .NET Core");
#else
                    SSPI = new SSPIHandler(Host, Krbsrvname, false);
                    passwordMessage = new PasswordMessage(SSPI.Continue(null));
                    break;
#endif

                case AuthenticationRequestType.AuthenticationGSSContinue:
#if DNXCORE50
                    throw new NotSupportedException("SSPI not yet supported in .NET Core");
#else
                    var passwdRead = SSPI.Continue(((AuthenticationGSSContinueMessage)msg).AuthenticationData);
                    if (passwdRead.Length != 0)
                    {
                        passwordMessage = new PasswordMessage(passwdRead);
                        break;
                    }
                    return;
#endif

                default:
                    throw new NotSupportedException(String.Format("Authentication method not supported (Received: {0})", msg.AuthRequestType));
            }
            passwordMessage.Write(Buffer);
            Buffer.Flush();
        }

        #endregion

        #region Frontend message processing

        internal void AddMessage(FrontendMessage msg)
        {
            _messagesToSend.Add(msg);
        }

        /// <summary>
        /// Prepends a message to be sent at the beginning of the next message chain.
        /// </summary>
        internal void PrependMessage(FrontendMessage msg)
        {
            if (msg is QueryMessage || msg is PregeneratedMessage || msg is SyncMessage)
            {
                // These messages produce a ReadyForQuery response, which we will be looking for when
                // processing the message chain results
                _pendingRfqPrependedMessages++;
            }
            _messagesToSend.Add(msg);
        }

        [GenerateAsync]
        internal void SendAllMessages()
        {
            _sentRfqPrependedMessages = _pendingRfqPrependedMessages;
            _pendingRfqPrependedMessages = 0;

            try
            {
                foreach (var msg in _messagesToSend)
                {
                    SendMessage(msg);
                }
                Buffer.Flush();
            }
            finally
            {
                _messagesToSend.Clear();
            }
        }

        /// <summary>
        /// Sends a single frontend message, used for simple messages such as rollback, etc.
        /// Note that additional prepend messages may be previously enqueued, and will be sent along
        /// with this message.
        /// </summary>
        /// <param name="msg"></param>
        internal void SendSingleMessage(FrontendMessage msg)
        {
            AddMessage(msg);
            SendAllMessages();
        }

        [GenerateAsync]
        void SendMessage(FrontendMessage msg)
        {
            try
            {
                Log.Trace(String.Format("Sending: {0}", msg), Id);

                var asSimple = msg as SimpleFrontendMessage;
                if (asSimple != null)
                {
                    if (asSimple.Length > Buffer.WriteSpaceLeft)
                    {
                        Buffer.Flush();
                    }
                    Contract.Assume(Buffer.WriteSpaceLeft >= asSimple.Length);
                    asSimple.Write(Buffer);
                    return;
                }

                var asComplex = msg as ChunkingFrontendMessage;
                if (asComplex != null)
                {
                    var directBuf = new DirectBuffer();
                    while (!asComplex.Write(Buffer, ref directBuf))
                    {
                        Buffer.Flush();

                        // The following is an optimization hack for writing large byte arrays without passing
                        // through our buffer
                        if (directBuf.Buffer != null)
                        {
                            Buffer.Underlying.Write(directBuf.Buffer, directBuf.Offset, directBuf.Size == 0 ? directBuf.Buffer.Length : directBuf.Size);
                            directBuf.Buffer = null;
                            directBuf.Size = 0;
                        }
                    }
                    return;
                }

                throw PGUtil.ThrowIfReached();
            }
            catch
            {
                Break();
                throw;
            }
        }

        #endregion

        #region Backend message processing

        [GenerateAsync]
        internal IBackendMessage ReadSingleMessage(DataRowLoadingMode dataRowLoadingMode = DataRowLoadingMode.NonSequential, bool ignoreNotifications = true)
        {
            try
            {
                // First read the responses of any prepended messages
                while (_sentRfqPrependedMessages > 0)
                {
                    var msg = DoReadSingleMessage(DataRowLoadingMode.Skip);
                    if (msg is ReadyForQueryMessage) {
                        _sentRfqPrependedMessages--;
                    }
                }

                return DoReadSingleMessage(dataRowLoadingMode, ignoreNotifications);
            }
            catch (NpgsqlException)
            {
                throw;
            }
            catch
            {
                Break();
                throw;
            }
        }

        [GenerateAsync]
        IBackendMessage DoReadSingleMessage(DataRowLoadingMode dataRowLoadingMode = DataRowLoadingMode.NonSequential, bool ignoreNotifications = true)
        {
            NpgsqlException error = null;

            while (true)
            {
                var buf = Buffer;

                Buffer.Ensure(5);
                var messageCode = (BackendMessageCode) Buffer.ReadByte();
                Contract.Assume(Enum.IsDefined(typeof(BackendMessageCode), messageCode), "Unknown message code: " + messageCode);
                var len = Buffer.ReadInt32() - 4;  // Transmitted length includes itself

                if ((messageCode == BackendMessageCode.DataRow || messageCode == BackendMessageCode.CopyData) &&
                    dataRowLoadingMode != DataRowLoadingMode.NonSequential)
                {
                    if (dataRowLoadingMode == DataRowLoadingMode.Skip)
                    {
                        Buffer.Skip(len);
                        continue;
                    }
                }
                else if (len > Buffer.ReadBytesLeft)
                {
                    buf = buf.EnsureOrAllocateTemp(len);
                }

                var msg = ParseServerMessage(buf, messageCode, len, dataRowLoadingMode);
                if (msg != null || !ignoreNotifications && (messageCode == BackendMessageCode.NoticeResponse || messageCode == BackendMessageCode.NotificationResponse))
                {
                    if (error != null)
                    {
                        Contract.Assert(messageCode == BackendMessageCode.ReadyForQuery, "Expected ReadyForQuery after ErrorResponse");
                        throw error;
                    }
                    return msg;
                }
                else if (messageCode == BackendMessageCode.ErrorResponse)
                {
                    // An ErrorResponse is (almost) always followed by a ReadyForQuery. Save the error
                    // and throw it as an exception when the ReadyForQuery is received (next).
                    error = new NpgsqlException(buf);

                    if (State == ConnectorState.Connecting) {
                        // During the startup/authentication phase, an ErrorResponse isn't followed by
                        // an RFQ. Instead, the server closes the connection immediately
                        throw error;
                    }

                    State = ConnectorState.Ready;
                }
            }
        }

        IBackendMessage ParseServerMessage(NpgsqlBuffer buf, BackendMessageCode code, int len, DataRowLoadingMode dataRowLoadingMode)
        {
            switch (code)
            {
                case BackendMessageCode.RowDescription:
                    // TODO: Recycle
                    var rowDescriptionMessage = new RowDescriptionMessage();
                    return rowDescriptionMessage.Load(buf, TypeHandlerRegistry);
                case BackendMessageCode.DataRow:
                    Contract.Assert(dataRowLoadingMode == DataRowLoadingMode.NonSequential || dataRowLoadingMode == DataRowLoadingMode.Sequential);
                    return dataRowLoadingMode == DataRowLoadingMode.Sequential
                        ? _dataRowSequentialMessage.Load(buf)
                        : _dataRowNonSequentialMessage.Load(buf);
                case BackendMessageCode.CompletedResponse:
                    return _commandCompleteMessage.Load(buf, len);
                case BackendMessageCode.ReadyForQuery:
                    var rfq = _readyForQueryMessage.Load(buf);
                    ProcessNewTransactionStatus(rfq.TransactionStatusIndicator);
                    return rfq;
                case BackendMessageCode.EmptyQueryResponse:
                    return EmptyQueryMessage.Instance;
                case BackendMessageCode.ParseComplete:
                    return ParseCompleteMessage.Instance;
                case BackendMessageCode.ParameterDescription:
                    return _parameterDescriptionMessage.Load(buf);
                case BackendMessageCode.BindComplete:
                    return BindCompleteMessage.Instance;
                case BackendMessageCode.NoData:
                    return NoDataMessage.Instance;
                case BackendMessageCode.CloseComplete:
                    return CloseCompletedMessage.Instance;
                case BackendMessageCode.ParameterStatus:
                    HandleParameterStatus(buf.ReadNullTerminatedString(), buf.ReadNullTerminatedString());
                    return null;
                case BackendMessageCode.NoticeResponse:
                    FireNotice(new NpgsqlNotice(buf));
                    return null;
                case BackendMessageCode.NotificationResponse:
                    FireNotification(new NpgsqlNotificationEventArgs(buf));
                    return null;

                case BackendMessageCode.AuthenticationRequest:
                    var authType = (AuthenticationRequestType)buf.ReadInt32();
                    Log.Trace("Received AuthenticationRequest of type " + authType, Id);
                    switch (authType)
                    {
                        case AuthenticationRequestType.AuthenticationOk:
                            return AuthenticationOkMessage.Instance;
                        case AuthenticationRequestType.AuthenticationCleartextPassword:
                            return AuthenticationCleartextPasswordMessage.Instance;
                        case AuthenticationRequestType.AuthenticationMD5Password:
                            return AuthenticationMD5PasswordMessage.Load(buf);
                        case AuthenticationRequestType.AuthenticationGSS:
                            return AuthenticationGSSMessage.Instance;
                        case AuthenticationRequestType.AuthenticationSSPI:
                            return AuthenticationSSPIMessage.Instance;
                        case AuthenticationRequestType.AuthenticationGSSContinue:
                            return AuthenticationGSSContinueMessage.Load(buf, len);
                        default:
                            throw new NotSupportedException(String.Format("Authentication method not supported (Received: {0})", authType));
                    }

                case BackendMessageCode.BackendKeyData:
                    BackendProcessId = buf.ReadInt32();
                    BackendSecretKey = buf.ReadInt32();
                    return null;

                case BackendMessageCode.CopyInResponse:
                    if (_copyInResponseMessage == null) {
                        _copyInResponseMessage = new CopyInResponseMessage();
                    }
                    return _copyInResponseMessage.Load(Buffer);

                case BackendMessageCode.CopyOutResponse:
                    if (_copyOutResponseMessage == null) {
                        _copyOutResponseMessage = new CopyOutResponseMessage();
                    }
                    return _copyOutResponseMessage.Load(Buffer);

                case BackendMessageCode.CopyData:
                    if (_copyDataMessage == null) {
                        _copyDataMessage = new CopyDataMessage();
                    }
                    return _copyDataMessage.Load(len);

                case BackendMessageCode.CopyDone:
                    return CopyDoneMessage.Instance;

                case BackendMessageCode.PortalSuspended:
                    throw new NotImplementedException("Unimplemented message: " + code);
                case BackendMessageCode.ErrorResponse:
                    return null;
                case BackendMessageCode.FunctionCallResponse:
                    // We don't use the obsolete function call protocol
                    throw new Exception("Unexpected backend message: " + code);
                default:
                    throw PGUtil.ThrowIfReached("Unknown backend message code: " + code);
            }
        }

        /// <summary>
        /// Reads backend messages and discards them, stopping only after a message of the given type has
        /// been seen.
        /// </summary>
        internal IBackendMessage SkipUntil(BackendMessageCode stopAt)
        {
            Contract.Requires(stopAt != BackendMessageCode.DataRow, "Shouldn't be used for rows, doesn't know about sequential");

            while (true)
            {
                var msg = ReadSingleMessage(DataRowLoadingMode.Skip);
                Contract.Assert(!(msg is DataRowMessage));
                if (msg.Code == stopAt) {
                    return msg;
                }
            }
        }

        /// <summary>
        /// Reads backend messages and discards them, stopping only after a message of the given types has
        /// been seen.
        /// </summary>
        internal IBackendMessage SkipUntil(BackendMessageCode stopAt1, BackendMessageCode stopAt2)
        {
            Contract.Requires(stopAt1 != BackendMessageCode.DataRow, "Shouldn't be used for rows, doesn't know about sequential");
            Contract.Requires(stopAt2 != BackendMessageCode.DataRow, "Shouldn't be used for rows, doesn't know about sequential");

            while (true) {
                var msg = ReadSingleMessage(DataRowLoadingMode.Skip);
                Contract.Assert(!(msg is DataRowMessage));
                if (msg.Code == stopAt1 || msg.Code == stopAt2) {
                    return msg;
                }
            }
        }

        /// <summary>
        /// Reads a single message, expecting it to be of type <typeparamref name="T"/>.
        /// Any other message causes an exception to be raised and the connector to be broken.
        /// Asynchronous messages (e.g. Notice) are treated and ignored. ErrorResponses raise an
        /// exception but do not cause the connector to break.
        /// </summary>
        internal T ReadExpecting<T>() where T : class, IBackendMessage
        {
            var msg = ReadSingleMessage();
            var asExpected = msg as T;
            if (asExpected == null)
            {
                Break();
                throw new Exception(String.Format("Unexpected message received when expecting {0}: {1}", typeof(T), msg.Code));
            }
            return asExpected;
        }

        #endregion Backend message processing

        #region Transactions

        internal bool InTransaction
        {
            get
            {
                switch (TransactionStatus)
                {
                case TransactionStatus.Idle:
                    return false;
                case TransactionStatus.Pending:
                case TransactionStatus.InTransactionBlock:
                case TransactionStatus.InFailedTransactionBlock:
                    return true;
                default:
                    throw PGUtil.ThrowIfReached();
                }
            }
        }
        /// <summary>
        /// Handles a new transaction indicator received on a ReadyForQuery message
        /// </summary>
        void ProcessNewTransactionStatus(TransactionStatus newStatus)
        {
            if (newStatus == TransactionStatus) { return; }

            switch (newStatus) {
            case TransactionStatus.Idle:
                if (TransactionStatus == TransactionStatus.Pending) {
                    // The transaction status must go from Pending through InTransactionBlock to Idle.
                    // And Idle received during Pending means that the transaction BEGIN message was prepended by another
                    // message (e.g. DISCARD ALL), whose RFQ had the (irrelevant) indicator Idle.
                    return;
                }
                ClearTransaction();
                break;
            case TransactionStatus.InTransactionBlock:
            case TransactionStatus.InFailedTransactionBlock:
                break;
            case TransactionStatus.Pending:
                throw new Exception("Invalid TransactionStatus (should be frontend-only)");
            default:
                throw PGUtil.ThrowIfReached();
            }
            TransactionStatus = newStatus;
        }

        internal void ClearTransaction()
        {
            if (TransactionStatus == TransactionStatus.Idle) { return; }
            // We may not have an NpgsqlTransaction for the transaction (i.e. user executed BEGIN)
            if (Transaction != null)
            {
                Transaction.Connection = null;
                Transaction = null;
            }
            TransactionStatus = TransactionStatus.Idle;
        }

        #endregion

        #region Notifications

        /// <summary>
        /// Occurs on NoticeResponses from the PostgreSQL backend.
        /// </summary>
        internal event NoticeEventHandler Notice;

        /// <summary>
        /// Occurs on NotificationResponses from the PostgreSQL backend.
        /// </summary>
        internal event NotificationEventHandler Notification;

        internal void FireNotice(NpgsqlNotice e)
        {
            var notice = Notice;
            if (notice != null)
            {
                try
                {
                    notice(this, new NpgsqlNoticeEventArgs(e));
                }
                catch
                {
                    // Ignore all exceptions bubbling up from the user's event handler
                }
            }
        }

        internal void FireNotification(NpgsqlNotificationEventArgs e)
        {
            var notification = Notification;
            if (notification != null)
            {
                try
                {
                    notification(this, e);
                }
                catch
                {
                    // Ignore all exceptions bubbling up from the user's event handler
                }
            }
        }

        #endregion Notifications

        #region SSL

        /// <summary>
        /// Default SSL ProvideClientCertificatesCallback implementation.
        /// </summary>
        internal void DefaultProvideClientCertificatesCallback(X509CertificateCollection certificates)
        {
            if (ProvideClientCertificatesCallback != null) {
                ProvideClientCertificatesCallback(certificates);
            }
        }

        /// <summary>
        /// Default SSL ValidateRemoteCertificateCallback implementation.
        /// </summary>
        internal bool DefaultValidateRemoteCertificateCallback(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            return ValidateRemoteCertificateCallback != null && ValidateRemoteCertificateCallback(cert, chain, errors);
        }

        /// <summary>
        /// Returns whether SSL is being used for the connection
        /// </summary>
        internal bool IsSecure { get; private set; }

        /// <summary>
        /// Called to provide client certificates for SSL handshake.
        /// </summary>
        internal event ProvideClientCertificatesCallback ProvideClientCertificatesCallback;

        /// <summary>
        /// Called to validate server's certificate during SSL handshake
        /// </summary>
        internal event ValidateRemoteCertificateCallback ValidateRemoteCertificateCallback;

        #endregion SSL

        #region Cancel

        /// <summary>
        /// Creates another connector and sends a cancel request through it for this connector.
        /// </summary>
        internal void CancelRequest()
        {
            var cancelConnector = new NpgsqlConnector(_settings);

            try
            {
                cancelConnector.RawOpen(cancelConnector.ConnectionTimeout*1000);
                cancelConnector.SendSingleMessage(new CancelRequestMessage(BackendProcessId, BackendSecretKey));
            }
            finally
            {
                cancelConnector.Close();
            }
        }

        #endregion Cancel

        #region Close / Reset

        /// <summary>
        /// Closes the physical connection to the server.
        /// </summary>
        internal void Close()
        {
            Log.Debug("Close connector", Id);

            switch (State)
            {
                case ConnectorState.Broken:
                case ConnectorState.Closed:
                    return;
                case ConnectorState.Ready:
                    try { SendSingleMessage(TerminateMessage.Instance); } catch {
                        // ignored
                    }
                break;
            }

            State = ConnectorState.Closed;
            Cleanup();
        }

        internal void Break()
        {
            State = ConnectorState.Broken;
            Cleanup();
        }

        /// <summary>
        /// Closes the socket and cleans up client-side resources associated with this connector.
        /// </summary>
        void Cleanup()
        {
            try { if (Stream != null) Stream.Close(); } catch {
                // ignored
            }

            try { RemoveNotificationListener(); } catch {
                // ignored
            }

            if (CurrentReader != null) {
                CurrentReader.Command.State = CommandState.Idle;
                try { CurrentReader.Close(); } catch {
                    // ignored
                }
                CurrentReader = null;
            }

            ClearTransaction();
            Stream = null;
            BaseStream = null;
            Buffer = null;
            Connection = null;
            BackendParams.Clear();
            ServerVersion = null;
        }

        /// <summary>
        /// Called when a pooled connection is closed, and its connector is returned to the pool.
        /// Resets the connector back to its initial state, releasing server-side sources
        /// (e.g. prepared statements), resetting parameters to their defaults, and resetting client-side
        /// state
        /// </summary>
        internal void Reset()
        {
            Contract.Requires(State == ConnectorState.Ready);

            Connection = null;

            switch (State)
            {
            case ConnectorState.Ready:
                break;
            case ConnectorState.Closed:
            case ConnectorState.Broken:
                Log.Warn(String.Format("Reset() called on connector with state {0}, ignoring", State), Id);
                return;
            case ConnectorState.Connecting:
            case ConnectorState.Executing:
            case ConnectorState.Fetching:
            case ConnectorState.Copy:
                throw new InvalidOperationException("Reset() called on connector with state " + State);
            default:
                throw PGUtil.ThrowIfReached();
            }

            // Must rollback transaction before sending DISCARD ALL
            if (InTransaction)
            {
                PrependMessage(PregeneratedMessage.RollbackTransaction);
                ClearTransaction();
            }

            if (SupportsDiscard)
            {
                PrependMessage(PregeneratedMessage.DiscardAll);
            }
            else
            {
                PrependMessage(PregeneratedMessage.UnlistenAll);
                /*
                 * Problem: we can't just deallocate for all the below since some may have already been deallocated
                 * Not sure if we should keep supporting this for < 8.3. If so fix along with #483
                if (_preparedStatementIndex > 0) {
                    for (var i = 1; i <= _preparedStatementIndex; i++) {
                        PrependMessage(new QueryMessage(String.Format("DEALLOCATE \"{0}{1}\";", PreparedStatementNamePrefix, i)));
                    }
                }*/

                _portalIndex = 0;
                _preparedStatementIndex = 0;
            }
        }

        #endregion Close

        #region Sync notification

        internal class NotificationBlock : IDisposable
        {
            NpgsqlConnector _connector;

            public NotificationBlock(NpgsqlConnector connector)
            {
                _connector = connector;
            }

            public void Dispose()
            {
                if (_connector != null && !_connector.IsBroken)
                {
                    if (--_connector._notificationBlockRecursionDepth == 0)
                    {
                        while (_connector.Buffer.ReadBytesLeft > 0
#if !DNXCORE50
                            || _connector.Stream is TlsClientStream.TlsClientStream && ((TlsClientStream.TlsClientStream)_connector.Stream).HasBufferedReadData(false)
#endif
                        )
                        {
                            var msg = _connector.ReadSingleMessage(DataRowLoadingMode.NonSequential, false);
                            if (msg != null)
                            {
                                Contract.Assert(msg == null, "Expected null after processing a notification");
                            }
                        }
                        if (_connector._notificationSemaphore != null)
                        {
                            _connector._notificationSemaphore.Release();
                        }
                    }
                }
                _connector = null;
            }
        }

        [GenerateAsync]
        internal NotificationBlock BlockNotifications()
        {
            var n = new NotificationBlock(this);
            if (++_notificationBlockRecursionDepth == 1 && _notificationSemaphore != null)
                _notificationSemaphore.Wait();
            return n;
        }

        internal void AddNotificationListener()
        {
            _notificationSemaphore = new SemaphoreSlim(1);
            var task = BaseStream.ReadAsync(EmptyBuffer, 0, 0);
            task.ContinueWith(NotificationHandler);
        }

        internal void RemoveNotificationListener()
        {
            _notificationSemaphore = null;
        }

        internal void NotificationHandler(System.Threading.Tasks.Task<int> task)
        {
            if (task.Exception != null || task.Result != 0)
            {
                // The stream is dead
                return;
            }

            var semaphore = _notificationSemaphore; // To avoid problems when closing the connection
            if (semaphore != null)
            {
                semaphore.WaitAsync().ContinueWith(t => {
                    try
                    {
                        while (Buffer.ReadBytesLeft > 0
#if !DNXCORE50
                            || Stream is TlsClientStream.TlsClientStream && ((TlsClientStream.TlsClientStream)Stream).HasBufferedReadData(true) || !IsSecure && BaseStream.DataAvailable
#endif
                        )
                        {
                            var msg = ReadSingleMessage(DataRowLoadingMode.NonSequential, false);
                            if (msg != null)
                            {
                                Contract.Assert(msg == null, "Expected null after processing a notification");
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        semaphore.Release();
                        try
                        {
                            BaseStream.ReadAsync(EmptyBuffer, 0, 0).ContinueWith(NotificationHandler);
                        }
                        catch { }
                    }
                });
            }
        }

        #endregion Sync notification

        #region Supported features

        internal bool SupportsApplicationName { get; private set; }
        internal bool SupportsExtraFloatDigits3 { get; private set; }
        internal bool SupportsExtraFloatDigits { get; private set; }
        internal bool SupportsSslRenegotiationLimit { get; private set; }
        internal bool SupportsSavepoint { get; private set; }
        internal bool SupportsDiscard { get; private set; }
        internal bool SupportsEStringPrefix { get; private set; }
        internal bool SupportsHexByteFormat { get; private set; }
        internal bool SupportsRangeTypes { get; private set; }
        internal bool UseConformantStrings { get; private set; }

        /// <summary>
        /// This method is required to set all the version dependent features flags.
        /// SupportsPrepare means the server can use prepared query plans (7.3+)
        /// </summary>
        void ProcessServerVersion()
        {
            SupportsSavepoint = (ServerVersion >= new Version(8, 0, 0));
            SupportsDiscard = (ServerVersion >= new Version(8, 3, 0));
            SupportsApplicationName = (ServerVersion >= new Version(9, 0, 0));
            SupportsExtraFloatDigits3 = (ServerVersion >= new Version(9, 0, 0));
            SupportsExtraFloatDigits = (ServerVersion >= new Version(7, 4, 0));
            SupportsSslRenegotiationLimit = ((ServerVersion >= new Version(8, 4, 3)) ||
                     (ServerVersion >= new Version(8, 3, 10) && ServerVersion < new Version(8, 4, 0)) ||
                     (ServerVersion >= new Version(8, 2, 16) && ServerVersion < new Version(8, 3, 0)) ||
                     (ServerVersion >= new Version(8, 1, 20) && ServerVersion < new Version(8, 2, 0)) ||
                     (ServerVersion >= new Version(8, 0, 24) && ServerVersion < new Version(8, 1, 0)) ||
                     (ServerVersion >= new Version(7, 4, 28) && ServerVersion < new Version(8, 0, 0)));

            // Per the PG documentation, E string literal prefix support appeared in PG version 8.1.
            // Note that it is possible that support for this prefix will vanish in some future version
            // of Postgres, in which case this test will need to be revised.
            // At that time it may also be necessary to set UseConformantStrings = true here.
            SupportsEStringPrefix = (ServerVersion >= new Version(8, 1, 0));

            // Per the PG documentation, hex string encoding format support appeared in PG version 9.0.
            SupportsHexByteFormat = (ServerVersion >= new Version(9, 0, 0));

            // Range data types
            SupportsRangeTypes = (ServerVersion >= new Version(9, 2, 0));
        }

        /// <summary>
        /// Whether the backend is an AWS Redshift instance
        /// </summary>
        internal bool IsRedshift
        {
            get { return _settings.ServerCompatibilityMode == ServerCompatibilityMode.Redshift; }
        }

        #endregion Supported features

        #region Execute blind

        /// <summary>
        /// Internal query shortcut for use in cases where the number
        /// of affected rows is of no interest.
        /// </summary>
        [GenerateAsync]
        internal void ExecuteBlind(string query)
        {
            using (BlockNotifications())
            {
                SetBackendCommandTimeout(20);
                SendSingleMessage(new QueryMessage(query));
                SkipUntil(BackendMessageCode.ReadyForQuery);
                State = ConnectorState.Ready;
            }
        }

        [GenerateAsync]
        internal void ExecuteBlind(SimpleFrontendMessage message)
        {
            using (BlockNotifications())
            {
                SetBackendCommandTimeout(20);
                SendSingleMessage(message);
                SkipUntil(BackendMessageCode.ReadyForQuery);
                State = ConnectorState.Ready;
            }
        }

        [GenerateAsync]
        internal void ExecuteBlindSuppressTimeout(string query)
        {
            using (BlockNotifications())
            {
                SendSingleMessage(new QueryMessage(query));
                SkipUntil(BackendMessageCode.ReadyForQuery);
                State = ConnectorState.Ready;
            }
        }

        [GenerateAsync]
        internal void ExecuteBlindSuppressTimeout(PregeneratedMessage message)
        {
            // Block the notification thread before writing anything to the wire.
            using (BlockNotifications())
            {
                SendSingleMessage(message);
                SkipUntil(BackendMessageCode.ReadyForQuery);
                State = ConnectorState.Ready;
            }
        }

        /// <summary>
        /// Special adaptation of ExecuteBlind() that sets statement_timeout.
        /// This exists to prevent Connector.SetBackendCommandTimeout() from calling Command.ExecuteBlind(),
        /// which will cause an endless recursive loop.
        /// </summary>
        /// <param name="timeout">Timeout in seconds.</param>
        [GenerateAsync]
        internal void ExecuteSetStatementTimeoutBlind(int timeout)
        {
            // Optimize for a few common timeout values.
            switch (timeout)
            {
                case 10:
                    SendSingleMessage(PregeneratedMessage.SetStmtTimeout10Sec);
                    break;

                case 20:
                    SendSingleMessage(PregeneratedMessage.SetStmtTimeout20Sec);
                    break;

                case 30:
                    SendSingleMessage(PregeneratedMessage.SetStmtTimeout30Sec);
                    break;

                case 60:
                    SendSingleMessage(PregeneratedMessage.SetStmtTimeout60Sec);
                    break;

                case 90:
                    SendSingleMessage(PregeneratedMessage.SetStmtTimeout90Sec);
                    break;

                case 120:
                    SendSingleMessage(PregeneratedMessage.SetStmtTimeout120Sec);
                    break;

                default:
                    SendSingleMessage(new QueryMessage(string.Format("SET statement_timeout = {0}", timeout * 1000)));
                    break;

            }
            SkipUntil(BackendMessageCode.ReadyForQuery);
            State = ConnectorState.Ready;
        }

        #endregion Execute blind

        #region Misc

        void HandleParameterStatus(string name, string value)
        {
            BackendParams[name] = value;

            if (name == "server_version")
            {
                // Deal with this here so that if there are
                // changes in a future backend version, we can handle it here in the
                // protocol handler and leave everybody else put of it.
                var versionString = value.Trim();
                for (var idx = 0; idx != versionString.Length; ++idx)
                {
                    var c = value[idx];
                    if (!char.IsDigit(c) && c != '.')
                    {
                        versionString = versionString.Substring(0, idx);
                        break;
                    }
                }
                ServerVersion = new Version(versionString);
                return;
            }

            if (name == "standard_conforming_strings") {
                UseConformantStrings = (value == "on");
            }
        }

        /// <summary>
        /// Modify the backend statement_timeout value if needed.
        /// </summary>
        /// <param name="timeout">New timeout</param>
        [GenerateAsync]
        internal void SetBackendCommandTimeout(int timeout)
        {
            /*
            if (Mediator.BackendCommandTimeout == -1 || Mediator.BackendCommandTimeout != timeout)
            {
                ExecuteSetStatementTimeoutBlind(timeout);
                Mediator.BackendCommandTimeout = timeout;
            }
             */
        }

        ///<summary>
        /// Returns next portal index.
        ///</summary>
        internal String NextPortalName()
        {
            return _portalNamePrefix + (++_portalIndex);
        }

        int _portalIndex;
        const String _portalNamePrefix = "p";

        ///<summary>
        /// Returns next plan index.
        ///</summary>
        internal string NextPreparedStatementName()
        {
            return PreparedStatementNamePrefix + (++_preparedStatementIndex);
        }

        int _preparedStatementIndex;
        const string PreparedStatementNamePrefix = "s";

        #endregion Misc

        #region Invariants

        [ContractInvariantMethod]
        void ObjectInvariants()
        {
        }

        #endregion
    }

    #region Enums

    /// <summary>
    /// Expresses the exact state of a connector.
    /// </summary>
    internal enum ConnectorState
    {
        /// <summary>
        /// The connector has either not yet been opened or has been closed.
        /// </summary>
        Closed,
        /// <summary>
        /// The connector is currently connecting to a Postgresql server.
        /// </summary>
        Connecting,
        /// <summary>
        /// The connector is connected and may be used to send a new query.
        /// </summary>
        Ready,
        /// <summary>
        /// The connector is waiting for a response to a query which has been sent to the server.
        /// </summary>
        Executing,
        /// <summary>
        /// The connector is currently fetching and processing query results.
        /// </summary>
        Fetching,
        /// <summary>
        /// The connection was broken because an unexpected error occurred which left it in an unknown state.
        /// This state isn't implemented yet.
        /// </summary>
        Broken,
        /// <summary>
        /// The connector is engaged in a COPY operation.
        /// </summary>
        Copy,
    }

    internal enum TransactionStatus : byte
    {
        /// <summary>
        /// Currently not in a transaction block
        /// </summary>
        Idle = (byte)'I',

        /// <summary>
        /// Currently in a transaction block
        /// </summary>
        InTransactionBlock = (byte)'T',

        /// <summary>
        /// Currently in a failed transaction block (queries will be rejected until block is ended)
        /// </summary>
        InFailedTransactionBlock = (byte)'E',

        /// <summary>
        /// A new transaction has been requested but not yet transmitted to the backend. It will be transmitted
        /// prepended to the next query.
        /// This is a client-side state option only, and is never transmitted from the backend.
        /// </summary>
        Pending = Byte.MaxValue,
    }

    /// <summary>
    /// Specifies how to load/parse DataRow messages as they're received from the backend.
    /// </summary>
    internal enum DataRowLoadingMode
    {
        /// <summary>
        /// Load DataRows in non-sequential mode
        /// </summary>
        NonSequential,
        /// <summary>
        /// Load DataRows in sequential mode
        /// </summary>
        Sequential,
        /// <summary>
        /// Skip DataRow messages altogether
        /// </summary>
        Skip
    }

    internal enum ServerCompatibilityMode
    {
        /// <summary>
        /// No special server compatibility mode is active
        /// </summary>
        None,
        /// <summary>
        /// The server is an Amazon Redshift instance.
        /// </summary>
        Redshift,
    }

    #endregion

    #region Delegates

    /// <summary>
    /// Represents the method that allows the application to provide a certificate collection to be used for SSL clien authentication
    /// </summary>
    /// <param name="certificates">A <see cref="System.Security.Cryptography.X509Certificates.X509CertificateCollection">X509CertificateCollection</see> to be filled with one or more client certificates.</param>
    public delegate void ProvideClientCertificatesCallback(X509CertificateCollection certificates);

    /// <summary>
    /// Represents the method that is called to validate the certificate provided by the server during an SSL handshake
    /// </summary>
    /// <param name="cert">The server's certificate</param>
    /// <param name="chain">The certificate chain containing the certificate's CA and any intermediate authorities</param>
    /// <param name="errors">Any errors that were detected</param>
    public delegate bool ValidateRemoteCertificateCallback(X509Certificate cert, X509Chain chain, SslPolicyErrors errors);

    #endregion
}
