﻿using Discord.API;
using Discord.Net.WebSockets;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Discord
{
	public enum DiscordClientState : byte
	{
		Disconnected,
		Connecting,
		Connected,
		Disconnecting
	}

	public class DisconnectedEventArgs : EventArgs
	{
		public readonly bool WasUnexpected;
		public readonly Exception Error;

		public DisconnectedEventArgs(bool wasUnexpected, Exception error)
		{
			WasUnexpected = wasUnexpected;
			Error = error;
		}
	}
	public sealed class LogMessageEventArgs : EventArgs
	{
		public LogSeverity Severity { get; }
		public string Source { get; }
		public string Message { get; }
		public Exception Exception { get; }

		public LogMessageEventArgs(LogSeverity severity, string source, string msg, Exception exception)
		{
			Severity = severity;
			Source = source;
			Message = msg;
			Exception = exception;
		}
	}

	/// <summary> Provides a connection to the DiscordApp service. </summary>
	public partial class DiscordClient
	{
		public static readonly string Version = typeof(DiscordClient).GetTypeInfo().Assembly.GetName().Version.ToString(3);

		private readonly ManualResetEvent _disconnectedEvent;
		private readonly ManualResetEventSlim _connectedEvent;
		private readonly Dictionary<Type, object> _singletons;
		private readonly LogService _log;
		private readonly object _cacheLock;
		private Logger _logger, _restLogger, _cacheLogger;
		private bool _sentInitialLog;
		private UserStatus _status;
		private int? _gameId;
		private Task _runTask;
		private ExceptionDispatchInfo _disconnectReason;
		private bool _wasDisconnectUnexpected;

		/// <summary> Returns the configuration object used to make this client. Note that this object cannot be edited directly - to change the configuration of this client, use the DiscordClient(DiscordClientConfig config) constructor. </summary>
		public DiscordConfig Config => _config;
		private readonly DiscordConfig _config;
		
		/// <summary> Returns the current connection state of this client. </summary>
		public DiscordClientState State => (DiscordClientState)_state;
		private int _state;

		/// <summary> Gives direct access to the underlying DiscordAPIClient. This can be used to modify objects not in cache. </summary>
		public DiscordAPIClient APIClient => _api;
		private readonly DiscordAPIClient _api;

		/// <summary> Returns the internal websocket object. </summary>
		public GatewaySocket WebSocket => _webSocket;
		private readonly GatewaySocket _webSocket;

		public string GatewayUrl => _gateway;
		private string _gateway;

		public string Token => _token;
		private string _token;

		/// <summary> Returns a cancellation token that triggers when the client is manually disconnected. </summary>
		public CancellationToken CancelToken => _cancelToken;
		private CancellationTokenSource _cancelTokenSource;
		private CancellationToken _cancelToken;

		public event EventHandler Connected;
		private void RaiseConnected()
		{
			if (Connected != null)
				EventHelper.Raise(_logger, nameof(Connected), () => Connected(this, EventArgs.Empty));
		}
		public event EventHandler<DisconnectedEventArgs> Disconnected;
		private void RaiseDisconnected(DisconnectedEventArgs e)
		{
			if (Disconnected != null)
				EventHelper.Raise(_logger, nameof(Disconnected), () => Disconnected(this, e));
		}

		/// <summary> Initializes a new instance of the DiscordClient class. </summary>
		public DiscordClient(DiscordConfig config = null)
		{
			_config = config ?? new DiscordConfig();
			_config.Lock();

			_nonceRand = new Random();
			_state = (int)DiscordClientState.Disconnected;
			_status = UserStatus.Online;

			//Services
			_singletons = new Dictionary<Type, object>();
			_log = AddService(new LogService());
			CreateMainLogger();

			//Async
			_cancelToken = new CancellationToken(true);
			_disconnectedEvent = new ManualResetEvent(true);
			_connectedEvent = new ManualResetEventSlim(false);
			
			//Cache
			_cacheLock = new object();
			_channels = new Channels(this, _cacheLock);
			_users = new Users(this, _cacheLock);
			_messages = new Messages(this, _cacheLock, Config.MessageCacheSize > 0);
			_roles = new Roles(this, _cacheLock);
			_servers = new Servers(this, _cacheLock);
			_globalUsers = new GlobalUsers(this, _cacheLock);
			CreateCacheLogger();

			//Networking
			_webSocket = new GatewaySocket(_config, _log.CreateLogger("WebSocket"));
            var settings = new JsonSerializerSettings();
            _webSocket.Connected += (s, e) =>
            {
                if (_state == (int)DiscordClientState.Connecting)
                    EndConnect();
            };
            _webSocket.Disconnected += (s, e) =>
            {
                RaiseDisconnected(e);
            };

            _webSocket.ReceivedDispatch += (s, e) => OnReceivedEvent(e);

            _api = new DiscordAPIClient(_config);
			if (Config.UseMessageQueue)
				_pendingMessages = new ConcurrentQueue<MessageQueueItem>();
			Connected += async (s, e) =>
			{
				_api.CancelToken = _cancelToken;
				await SendStatus().ConfigureAwait(false);
			};
			CreateRestLogger();

			//Import/Export
			_messageImporter = new JsonSerializer();
			_messageImporter.ContractResolver = new Message.ImportResolver();
		}

		private void CreateMainLogger()
		{
			_logger = _log.CreateLogger("Client");
			if (_logger.Level >= LogSeverity.Info)
			{
				JoinedServer += (s, e) => _logger.Info($"Server Created: {e.Server?.Name ?? "[Private]"}");
				LeftServer += (s, e) => _logger.Info($"Server Destroyed: {e.Server?.Name ?? "[Private]"}");
				ServerUpdated += (s, e) => _logger.Info($"Server Updated: {e.Server?.Name ?? "[Private]"}");
				ServerAvailable += (s, e) => _logger.Info($"Server Available: {e.Server?.Name ?? "[Private]"}");
				ServerUnavailable += (s, e) => _logger.Info($"Server Unavailable: {e.Server?.Name ?? "[Private]"}");
				ChannelCreated += (s, e) => _logger.Info($"Channel Created: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}");
				ChannelDestroyed += (s, e) => _logger.Info($"Channel Destroyed: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}");
				ChannelUpdated += (s, e) => _logger.Info($"Channel Updated: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}");
				MessageReceived += (s, e) => _logger.Info($"Message Received: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}/{e.Message?.Id}");
				MessageDeleted += (s, e) => _logger.Info($"Message Deleted: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}/{e.Message?.Id}");
				MessageUpdated += (s, e) => _logger.Info($"Message Update: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}/{e.Message?.Id}");
				RoleCreated += (s, e) => _logger.Info($"Role Created: {e.Server?.Name ?? "[Private]"}/{e.Role?.Name}");
				RoleUpdated += (s, e) => _logger.Info($"Role Updated: {e.Server?.Name ?? "[Private]"}/{e.Role?.Name}");
				RoleDeleted += (s, e) => _logger.Info($"Role Deleted: {e.Server?.Name ?? "[Private]"}/{e.Role?.Name}");
				UserBanned += (s, e) => _logger.Info($"Banned User: {e.Server?.Name ?? "[Private]" }/{e.UserId}");
				UserUnbanned += (s, e) => _logger.Info($"Unbanned User: {e.Server?.Name ?? "[Private]"}/{e.UserId}");
				UserJoined += (s, e) => _logger.Info($"User Joined: {e.Server?.Name ?? "[Private]"}/{e.User.Name}");
				UserLeft += (s, e) => _logger.Info($"User Left: {e.Server?.Name ?? "[Private]"}/{e.User.Name}");
				UserUpdated += (s, e) => _logger.Info($"User Updated: {e.Server?.Name ?? "[Private]"}/{e.User.Name}");
				UserVoiceStateUpdated += (s, e) => _logger.Info($"Voice Updated: {e.Server?.Name ?? "[Private]"}/{e.User.Name}");
				ProfileUpdated += (s, e) => _logger.Info("Profile Updated");
			}
			if (_log.Level >= LogSeverity.Verbose)
			{
				UserIsTypingUpdated += (s, e) => _logger.Verbose($"Is Typing: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}/{e.User?.Name}");
				MessageAcknowledged += (s, e) => _logger.Verbose($"Ack Message: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}/{e.Message?.Id}");
				MessageSent += (s, e) => _logger.Verbose($"Sent Message: {e.Server?.Name ?? "[Private]"}/{e.Channel?.Name}/{e.Message?.Id}");
				UserPresenceUpdated += (s, e) => _logger.Verbose($"Presence Updated: {e.Server?.Name ?? "[Private]"}/{e.User?.Name}");
			}
		}
		private void CreateRestLogger()
		{
			_restLogger = _log.CreateLogger("Rest");
			if (_log.Level >= LogSeverity.Verbose)
			{
				_api.RestClient.OnRequest += (s, e) =>
				{
					if (e.Payload != null)
						_restLogger.Verbose( $"{e.Method} {e.Path}: {Math.Round(e.ElapsedMilliseconds, 2)} ms ({e.Payload})");
					else
						_restLogger.Verbose( $"{e.Method} {e.Path}: {Math.Round(e.ElapsedMilliseconds, 2)} ms");
				};
			}
		}
		private void CreateCacheLogger()
		{
			_cacheLogger = _log.CreateLogger("Cache");
			if (_log.Level >= LogSeverity.Debug)
			{
				_channels.ItemCreated += (s, e) => _cacheLogger.Debug( $"Created Channel {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Id}");
				_channels.ItemDestroyed += (s, e) => _cacheLogger.Debug( $"Destroyed Channel {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Id}");
				_channels.Cleared += (s, e) => _cacheLogger.Debug( $"Cleared Channels");
				_users.ItemCreated += (s, e) => _cacheLogger.Debug( $"Created User {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Id}");
				_users.ItemDestroyed += (s, e) => _cacheLogger.Debug( $"Destroyed User {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Id}");
				_users.Cleared += (s, e) => _cacheLogger.Debug( $"Cleared Users");
				_messages.ItemCreated += (s, e) => _cacheLogger.Debug( $"Created Message {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Channel.Id}/{e.Item.Id}");
				_messages.ItemDestroyed += (s, e) => _cacheLogger.Debug( $"Destroyed Message {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Channel.Id}/{e.Item.Id}");
				_messages.ItemRemapped += (s, e) => _cacheLogger.Debug( $"Remapped Message {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Channel.Id}/[{e.OldId} -> {e.NewId}]");
				_messages.Cleared += (s, e) => _cacheLogger.Debug( $"Cleared Messages");
				_roles.ItemCreated += (s, e) => _cacheLogger.Debug( $"Created Role {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Id}");
				_roles.ItemDestroyed += (s, e) => _cacheLogger.Debug( $"Destroyed Role {IdConvert.ToString(e.Item.Server?.Id) ?? "[Private]"}/{e.Item.Id}");
				_roles.Cleared += (s, e) => _cacheLogger.Debug( $"Cleared Roles");
				_servers.ItemCreated += (s, e) => _cacheLogger.Debug( $"Created Server {e.Item.Id}");
				_servers.ItemDestroyed += (s, e) => _cacheLogger.Debug( $"Destroyed Server {e.Item.Id}");
				_servers.Cleared += (s, e) => _cacheLogger.Debug( $"Cleared Servers");
				_globalUsers.ItemCreated += (s, e) => _cacheLogger.Debug( $"Created User {e.Item.Id}");
				_globalUsers.ItemDestroyed += (s, e) => _cacheLogger.Debug( $"Destroyed User {e.Item.Id}");
				_globalUsers.Cleared += (s, e) => _cacheLogger.Debug( $"Cleared Users");
			}
		}

		/// <summary> Connects to the Discord server with the provided email and password. </summary>
		/// <returns> Returns a token for future connections. </returns>
		public async Task<string> Connect(string email, string password)
		{
			if (!_sentInitialLog)
				SendInitialLog();

			if (State != DiscordClientState.Disconnected)
				await Disconnect().ConfigureAwait(false);
			
			var response = await _api.Login(email, password)
				.ConfigureAwait(false);
            _token = response.Token;
            _api.Token = response.Token;
            if (_config.LogLevel >= LogSeverity.Verbose)
				_logger.Verbose( "Login successful, got token.");

            await BeginConnect();
            return response.Token;
        }
		/// <summary> Connects to the Discord server with the provided token. </summary>
		public async Task Connect(string token)
        {
            if (!_sentInitialLog)
                SendInitialLog();

            if (State != (int)DiscordClientState.Disconnected)
                await Disconnect().ConfigureAwait(false);

            _token = token;
            _api.Token = token;
            await BeginConnect();
        }

        private async Task BeginConnect()
        {
            try
            {
                _state = (int)DiscordClientState.Connecting;

                var gatewayResponse = await _api.Gateway().ConfigureAwait(false);
                string gateway = gatewayResponse.Url;
                if (_config.LogLevel >= LogSeverity.Verbose)
                    _logger.Verbose( $"Websocket endpoint: {gateway}");

                _disconnectedEvent.Reset();

                _gateway = gateway;

                _cancelTokenSource = new CancellationTokenSource();
                _cancelToken = _cancelTokenSource.Token;

                _webSocket.Host = gateway;
                _webSocket.ParentCancelToken = _cancelToken;
                await _webSocket.Connect(_token).ConfigureAwait(false);

                _runTask = RunTasks();

                try
                {
                    //Cancel if either Disconnect is called, data socket errors or timeout is reached
                    var cancelToken = CancellationTokenSource.CreateLinkedTokenSource(_cancelToken, _webSocket.CancelToken).Token;
                    _connectedEvent.Wait(cancelToken);
                }
                catch (OperationCanceledException)
                {
                    _webSocket.ThrowError(); //Throws data socket's internal error if any occured
                    throw;
                }
            }
            catch
            {
                await Disconnect().ConfigureAwait(false);
                throw;
            }
        }
		private void EndConnect()
		{
			_state = (int)DiscordClientState.Connected;
			_connectedEvent.Set();
			RaiseConnected();
		}

		/// <summary> Disconnects from the Discord server, canceling any pending requests. </summary>
		public Task Disconnect() => SignalDisconnect(new Exception("Disconnect was requested by user."), isUnexpected: false);
		private async Task SignalDisconnect(Exception ex = null, bool isUnexpected = true, bool wait = false)
		{
			int oldState;
			bool hasWriterLock;

			//If in either connecting or connected state, get a lock by being the first to switch to disconnecting
			oldState = Interlocked.CompareExchange(ref _state, (int)DiscordClientState.Disconnecting, (int)DiscordClientState.Connecting);
			if (oldState == (int)DiscordClientState.Disconnected) return; //Already disconnected
			hasWriterLock = oldState == (int)DiscordClientState.Connecting; //Caused state change
			if (!hasWriterLock)
			{
				oldState = Interlocked.CompareExchange(ref _state, (int)DiscordClientState.Disconnecting, (int)DiscordClientState.Connected);
				if (oldState == (int)DiscordClientState.Disconnected) return; //Already disconnected
				hasWriterLock = oldState == (int)DiscordClientState.Connected; //Caused state change
			}

			if (hasWriterLock)
			{
				_wasDisconnectUnexpected = isUnexpected;
				_disconnectReason = ex != null ? ExceptionDispatchInfo.Capture(ex) : null;

				_cancelTokenSource.Cancel();
				/*if (_disconnectState == DiscordClientState.Connecting) //_runTask was never made
					await Cleanup().ConfigureAwait(false);*/
			}

			if (wait)
			{
				Task task = _runTask;
				if (_runTask != null)
					await task.ConfigureAwait(false);
			}
		}

		private async Task RunTasks()
		{
			List<Task> tasks = new List<Task>();
			tasks.Add(_cancelToken.Wait());
			if (_config.UseMessageQueue)
				tasks.Add(MessageQueueAsync());

			Task[] tasksArray = tasks.ToArray();
			Task firstTask = Task.WhenAny(tasksArray);
			Task allTasks = Task.WhenAll(tasksArray);

			//Wait until the first task ends/errors and capture the error
			try { await firstTask.ConfigureAwait(false); }
			catch (Exception ex) { await SignalDisconnect(ex: ex, wait: true).ConfigureAwait(false); }

			//Ensure all other tasks are signaled to end.
			await SignalDisconnect(wait: true).ConfigureAwait(false);

			//Wait for the remaining tasks to complete
			try { await allTasks.ConfigureAwait(false); }
			catch { }

			//Start cleanup
			var wasDisconnectUnexpected = _wasDisconnectUnexpected;
			_wasDisconnectUnexpected = false;

			await _webSocket.SignalDisconnect().ConfigureAwait(false);

			_privateUser = null;
			_gateway = null;
			_token = null;

			if (!wasDisconnectUnexpected)
			{
				_state = (int)DiscordClientState.Disconnected;
				_disconnectedEvent.Set();
			}
			_connectedEvent.Reset();
			_runTask = null;
		}
		private async Task Stop()
		{
			if (Config.UseMessageQueue)
			{
				MessageQueueItem ignored;
				while (_pendingMessages.TryDequeue(out ignored)) { }
			}

			await _api.Logout().ConfigureAwait(false);

			_channels.Clear();
			_users.Clear();
			_messages.Clear();
			_roles.Clear();
			_servers.Clear();
			_globalUsers.Clear();

			_privateUser = null;
		}
		
		private void OnReceivedEvent(WebSocketEventEventArgs e)
		{
			try
			{
				switch (e.Type)
				{
					//Global
					case "READY": //Resync 
						{
							var data = e.Payload.ToObject<ReadyEvent>(_webSocket.Serializer);
							_privateUser = _users.GetOrAdd(data.User.Id, null);
							_privateUser.Update(data.User);
							_privateUser.Global.Update(data.User);
                            foreach (var model in data.Guilds)
							{
								if (model.Unavailable != true)
								{
									var server = _servers.GetOrAdd(model.Id);
									server.Update(model);
								}
							}
							foreach (var model in data.PrivateChannels)
							{
								var user = _users.GetOrAdd(model.Recipient.Id, null);
								user.Update(model.Recipient);
								var channel = _channels.GetOrAdd(model.Id, null, user.Id);
								channel.Update(model);
							}
						}
						break;

					//Servers
					case "GUILD_CREATE":
						{
							var data = e.Payload.ToObject<GuildCreateEvent>(_webSocket.Serializer);
							if (data.Unavailable != true)
							{
								var server = _servers.GetOrAdd(data.Id);
								server.Update(data);
								if (data.Unavailable == false)
									RaiseServerAvailable(server);
								else
									RaiseJoinedServer(server);
							}
						}
						break;
					case "GUILD_UPDATE":
						{
							var data = e.Payload.ToObject<GuildUpdateEvent>(_webSocket.Serializer);
							var server = _servers[data.Id];
							if (server != null)
							{
								server.Update(data);
								RaiseServerUpdated(server);
							}
						}
						break;
					case "GUILD_DELETE":
						{
							var data = e.Payload.ToObject<GuildDeleteEvent>(_webSocket.Serializer);
							var server = _servers.TryRemove(data.Id);
							if (server != null)
							{
								if (data.Unavailable == true)
									RaiseServerUnavailable(server);
								else
									RaiseLeftServer(server);
							}
						}
						break;

					//Channels
					case "CHANNEL_CREATE":
						{
							var data = e.Payload.ToObject<ChannelCreateEvent>(_webSocket.Serializer);
							Channel channel;
							if (data.IsPrivate)
							{
								var user = _users.GetOrAdd(data.Recipient.Id, null);
								user.Update(data.Recipient);
								channel = _channels.GetOrAdd(data.Id, null, user.Id);
							}
							else
								channel = _channels.GetOrAdd(data.Id, data.GuildId, null);
							channel.Update(data);
							RaiseChannelCreated(channel);
						}
						break;
					case "CHANNEL_UPDATE":
						{
							var data = e.Payload.ToObject<ChannelUpdateEvent>(_webSocket.Serializer);
							var channel = _channels[data.Id];
							if (channel != null)
							{
								channel.Update(data);
								RaiseChannelUpdated(channel);
							}
						}
						break;
					case "CHANNEL_DELETE":
						{
							var data = e.Payload.ToObject<ChannelDeleteEvent>(_webSocket.Serializer);
							var channel = _channels.TryRemove(data.Id);
							if (channel != null)
								RaiseChannelDestroyed(channel);
						}
						break;

					//Members
					case "GUILD_MEMBER_ADD":
						{
							var data = e.Payload.ToObject<MemberAddEvent>(_webSocket.Serializer);
							var user = _users.GetOrAdd(data.User.Id, data.GuildId);
							user.Update(data);
							user.UpdateActivity();
							RaiseUserJoined(user);
						}
						break;
					case "GUILD_MEMBER_UPDATE":
						{
							var data = e.Payload.ToObject<MemberUpdateEvent>(_webSocket.Serializer);
							var user = _users[data.User.Id, data.GuildId];
							if (user != null)
							{
								user.Update(data);
								RaiseUserUpdated(user);
							}
						}
						break;
					case "GUILD_MEMBER_REMOVE":
						{
							var data = e.Payload.ToObject<MemberRemoveEvent>(_webSocket.Serializer);
							var user = _users.TryRemove(data.UserId, data.GuildId);
							if (user != null)
								RaiseUserLeft(user);
						}
						break;
					case "GUILD_MEMBERS_CHUNK":
						{
							var data = e.Payload.ToObject<MembersChunkEvent>(_webSocket.Serializer);
							foreach (var memberData in data.Members)
							{
								var user = _users.GetOrAdd(memberData.User.Id, memberData.GuildId);
								user.Update(memberData);
								//RaiseUserAdded(user);
							}
						}
						break;

					//Roles
					case "GUILD_ROLE_CREATE":
						{
							var data = e.Payload.ToObject<RoleCreateEvent>(_webSocket.Serializer);
							var role = _roles.GetOrAdd(data.Data.Id, data.GuildId);
							role.Update(data.Data);
							var server = _servers[data.GuildId];
							if (server != null)
								server.AddRole(role);
							RaiseRoleUpdated(role);
						}
						break;
					case "GUILD_ROLE_UPDATE":
						{
							var data = e.Payload.ToObject<RoleUpdateEvent>(_webSocket.Serializer);
							var role = _roles[data.Data.Id];
							if (role != null)
							{
								role.Update(data.Data);
								RaiseRoleUpdated(role);
							}
						}
						break;
					case "GUILD_ROLE_DELETE":
						{
							var data = e.Payload.ToObject<RoleDeleteEvent>(_webSocket.Serializer);
							var role = _roles.TryRemove(data.RoleId);
							if (role != null)
							{
								RaiseRoleDeleted(role);
								var server = _servers[data.GuildId];
								if (server != null)
									server.RemoveRole(role);
							}
						}
						break;

					//Bans
					case "GUILD_BAN_ADD":
						{
							var data = e.Payload.ToObject<BanAddEvent>(_webSocket.Serializer);
							var server = _servers[data.GuildId];
							if (server != null)
							{
								var id = data.User?.Id ?? data.UserId;
                                server.AddBan(id);
								RaiseUserBanned(id, server);
							}
						}
						break;
					case "GUILD_BAN_REMOVE":
						{
							var data = e.Payload.ToObject<BanRemoveEvent>(_webSocket.Serializer);
							var server = _servers[data.GuildId];
							if (server != null)
							{
								var id = data.User?.Id ?? data.UserId;
								if (server.RemoveBan(id))
									RaiseUserUnbanned(id, server);
							}
						}
						break;

					//Messages
					case "MESSAGE_CREATE":
						{
							var data = e.Payload.ToObject<MessageCreateEvent>(_webSocket.Serializer);
							Message msg = null;

							bool isAuthor = data.Author.Id == _privateUser.Id;
                            int nonce = 0;

                            if (data.Author.Id == _privateUser.Id && Config.UseMessageQueue)
                            {
                                if (data.Nonce != null && int.TryParse(data.Nonce, out nonce))
                                    msg = _messages[nonce];
                            }
                            if (msg == null)
                            {
                                msg = _messages.GetOrAdd(data.Id, data.ChannelId, data.Author.Id);
                                nonce = 0;
                            }

							msg.Update(data);
							var user = msg.User;
                            if (user != null)
                                user.UpdateActivity();// data.Timestamp);

                            //Remapped queued message
                            if (nonce != 0)
                            {
                                msg = _messages.Remap(nonce, data.Id);
                                msg.Id = data.Id;
                            }

                            msg.State = MessageState.Normal;
                            RaiseMessageReceived(msg);
						}
						break;
					case "MESSAGE_UPDATE":
						{
							var data = e.Payload.ToObject<MessageUpdateEvent>(_webSocket.Serializer);
							var msg = _messages[data.Id];
							if (msg != null)
                            {
                                msg.Update(data);
                                msg.State = MessageState.Normal;
                                RaiseMessageUpdated(msg);
							}
						}
						break;
					case "MESSAGE_DELETE":
						{
							var data = e.Payload.ToObject<MessageDeleteEvent>(_webSocket.Serializer);
							var msg = _messages.TryRemove(data.Id);
							if (msg != null)
								RaiseMessageDeleted(msg);
						}
						break;
					case "MESSAGE_ACK":
						{
							var data = e.Payload.ToObject<MessageAckEvent>(_webSocket.Serializer);
							var msg = GetMessage(data.MessageId);
							if (msg != null)
								RaiseMessageAcknowledged(msg);
						}
						break;

					//Statuses
					case "PRESENCE_UPDATE":
						{
							var data = e.Payload.ToObject<PresenceUpdateEvent>(_webSocket.Serializer);
							var user = _users.GetOrAdd(data.User.Id, data.GuildId);
							if (user != null)
							{
								user.Update(data);
								RaiseUserPresenceUpdated(user);
							}
						}
						break;
					case "TYPING_START":
						{
							var data = e.Payload.ToObject<TypingStartEvent>(_webSocket.Serializer);
							var channel = _channels[data.ChannelId];
							if (channel != null)
							{
								var user = _users[data.UserId, channel.Server?.Id];
								if (user != null)
								{
									if (channel != null)
										RaiseUserIsTyping(user, channel);                                
									user.UpdateActivity();
                                }
                            }
						}
						break;

					//Voice
					case "VOICE_STATE_UPDATE":
						{
							var data = e.Payload.ToObject<MemberVoiceStateUpdateEvent>(_webSocket.Serializer);
							var user = _users[data.UserId, data.GuildId];
							if (user != null)
							{
								/*var voiceChannel = user.VoiceChannel;
                                if (voiceChannel != null && data.ChannelId != voiceChannel.Id && user.IsSpeaking)
								{
									user.IsSpeaking = false;
									RaiseUserIsSpeaking(user, _channels[voiceChannel.Id], false);
								}*/
								user.Update(data);
								RaiseUserVoiceStateUpdated(user);
							}
						}
						break;

					//Settings
					case "USER_UPDATE":
						{
							var data = e.Payload.ToObject<UserUpdateEvent>(_webSocket.Serializer);
							var globalUser = _globalUsers[data.Id];
							if (globalUser != null)
							{
								globalUser.Update(data);
								foreach (var user in globalUser.Memberships)
									user.Update(data);
								RaiseProfileUpdated();
							}
						}
						break;

					//Ignored
					case "USER_SETTINGS_UPDATE":
					case "GUILD_INTEGRATIONS_UPDATE":
					case "VOICE_SERVER_UPDATE":
						break;
						
					case "RESUMED": //Handled in DataWebSocket
						break;

					//Others
					default:
						_webSocket.Logger.Log(LogSeverity.Warning, $"Unknown message type: {e.Type}");
						break;
				}
			}
			catch (Exception ex)
			{
				_logger.Log(LogSeverity.Error, $"Error handling {e.Type} event", ex);
			}
		}

		private void SendInitialLog()
		{
			if (_config.LogLevel >= LogSeverity.Verbose)
				_logger.Verbose( $"Config: {JsonConvert.SerializeObject(_config)}");
			_sentInitialLog = true;
        }

        #region Async Wrapper
        /// <summary> Blocking call that will not return until client has been stopped. This is mainly intended for use in console applications. </summary>
        public void Run(Func<Task> asyncAction)
        {
            try
            {
                asyncAction().GetAwaiter().GetResult(); //Avoids creating AggregateExceptions
            }
            catch (TaskCanceledException) { }
            _disconnectedEvent.WaitOne();
        }
        /// <summary> Blocking call that will not return until client has been stopped. This is mainly intended for use in console applications. </summary>
        public void Run()
        {
            _disconnectedEvent.WaitOne();
        }
        #endregion

        #region Services
        public T AddSingleton<T>(T obj)
            where T : class
        {
            _singletons.Add(typeof(T), obj);
            return obj;
        }
        public T GetSingleton<T>(bool required = true)
            where T : class
        {
            object singleton;
            T singletonT = null;
            if (_singletons.TryGetValue(typeof(T), out singleton))
                singletonT = singleton as T;

            if (singletonT == null && required)
                throw new InvalidOperationException($"This operation requires {typeof(T).Name} to be added to {nameof(DiscordClient)}.");
            return singletonT;
        }
        public T AddService<T>(T obj)
            where T : class, IService
        {
            AddSingleton(obj);
            obj.Install(this);
            return obj;
        }
        public T GetService<T>(bool required = true)
            where T : class, IService
            => GetSingleton<T>(required);
        #endregion

        #region IDisposable
        private bool _isDisposed = false;

        protected virtual void Dispose(bool isDisposing)
        {
            if (!_isDisposed)
            {
                if (isDisposing)
                {
                    _disconnectedEvent.Dispose();
                    _connectedEvent.Dispose();
                }
                _isDisposed = true;
            }
        }
        
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion

        //Helpers
        private void CheckReady()
        {
            switch (_state)
            {
                case (int)DiscordClientState.Disconnecting:
                    throw new InvalidOperationException("The client is disconnecting.");
                case (int)DiscordClientState.Disconnected:
                    throw new InvalidOperationException("The client is not connected to Discord");
                case (int)DiscordClientState.Connecting:
                    throw new InvalidOperationException("The client is connecting.");
            }
        }

        public void GetCacheStats(out int serverCount, out int channelCount, out int userCount, out int uniqueUserCount, out int messageCount, out int roleCount)
        {
            serverCount = _servers.Count;
            channelCount = _channels.Count;
            userCount = _users.Count;
            uniqueUserCount = _globalUsers.Count;
            messageCount = _messages.Count;
            roleCount = _roles.Count;
        }
    }
}