﻿using Discord.API.Rest;
using Discord.Rest;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Model = Discord.API.Channel;

namespace Discord.WebSocket
{
    [DebuggerDisplay(@"{DebuggerDisplay,nq}")]
    public abstract class SocketGuildChannel : SocketChannel, IGuildChannel
    {
        private ImmutableArray<Overwrite> _overwrites;

        public SocketGuild Guild { get; }
        public string Name { get; private set; }
        public int Position { get; private set; }

        public IReadOnlyCollection<Overwrite> PermissionOverwrites => _overwrites;
        public new abstract IReadOnlyCollection<SocketGuildUser> Users { get; }

        internal SocketGuildChannel(DiscordSocketClient discord, ulong id, SocketGuild guild)
            : base(discord, id)
        {
            Guild = guild;
        }
        internal static SocketGuildChannel Create(SocketGuild guild, ClientState state, Model model)
        {
            switch (model.Type)
            {
                case ChannelType.Text:
                    return SocketTextChannel.Create(guild, state, model);
                case ChannelType.Voice:
                    return SocketVoiceChannel.Create(guild, state, model);
                default:
                    throw new InvalidOperationException("Unknown guild channel type");
            }
        }
        internal override void Update(ClientState state, Model model)
        {
            Name = model.Name.Value;
            Position = model.Position.Value;

            var overwrites = model.PermissionOverwrites.Value;
            var newOverwrites = ImmutableArray.CreateBuilder<Overwrite>(overwrites.Length);
            for (int i = 0; i < overwrites.Length; i++)
                newOverwrites.Add(new Overwrite(overwrites[i]));
            _overwrites = newOverwrites.ToImmutable();
        }
        
        public Task ModifyAsync(Action<ModifyGuildChannelParams> func)
            => ChannelHelper.ModifyAsync(this, Discord, func);
        public Task DeleteAsync()
            => ChannelHelper.DeleteAsync(this, Discord);

        public OverwritePermissions? GetPermissionOverwrite(IUser user)
        {
            for (int i = 0; i < _overwrites.Length; i++)
            {
                if (_overwrites[i].TargetId == user.Id)
                    return _overwrites[i].Permissions;
            }
            return null;
        }
        public OverwritePermissions? GetPermissionOverwrite(IRole role)
        {
            for (int i = 0; i < _overwrites.Length; i++)
            {
                if (_overwrites[i].TargetId == role.Id)
                    return _overwrites[i].Permissions;
            }
            return null;
        }
        public async Task AddPermissionOverwriteAsync(IUser user, OverwritePermissions perms)
        {
            await ChannelHelper.AddPermissionOverwriteAsync(this, Discord, user, perms).ConfigureAwait(false);
            _overwrites = _overwrites.Add(new Overwrite(new API.Overwrite { Allow = perms.AllowValue, Deny = perms.DenyValue, TargetId = user.Id, TargetType = PermissionTarget.User }));
        }
        public async Task AddPermissionOverwriteAsync(IRole role, OverwritePermissions perms)
        {
            await ChannelHelper.AddPermissionOverwriteAsync(this, Discord, role, perms).ConfigureAwait(false);
            _overwrites.Add(new Overwrite(new API.Overwrite { Allow = perms.AllowValue, Deny = perms.DenyValue, TargetId = role.Id, TargetType = PermissionTarget.Role }));
        }
        public async Task RemovePermissionOverwriteAsync(IUser user)
        {
            await ChannelHelper.RemovePermissionOverwriteAsync(this, Discord, user).ConfigureAwait(false);

            for (int i = 0; i < _overwrites.Length; i++)
            {
                if (_overwrites[i].TargetId == user.Id)
                {
                    _overwrites = _overwrites.RemoveAt(i);
                    return;
                }
            }
        }
        public async Task RemovePermissionOverwriteAsync(IRole role)
        {
            await ChannelHelper.RemovePermissionOverwriteAsync(this, Discord, role).ConfigureAwait(false);

            for (int i = 0; i < _overwrites.Length; i++)
            {
                if (_overwrites[i].TargetId == role.Id)
                {
                    _overwrites = _overwrites.RemoveAt(i);
                    return;
                }
            }
        }

        public async Task<IReadOnlyCollection<RestInviteMetadata>> GetInvitesAsync()
            => await ChannelHelper.GetInvitesAsync(this, Discord);
        public async Task<RestInviteMetadata> CreateInviteAsync(int? maxAge = 3600, int? maxUses = null, bool isTemporary = true)
            => await ChannelHelper.CreateInviteAsync(this, Discord, maxAge, maxUses, isTemporary);

        public new abstract SocketGuildUser GetUser(ulong id);

        public override string ToString() => Name;
        internal new SocketGuildChannel Clone() => MemberwiseClone() as SocketGuildChannel;

        //SocketChannel
        internal override IReadOnlyCollection<SocketUser> GetUsersInternal() => Users;
        internal override SocketUser GetUserInternal(ulong id) => GetUser(id);

        //IGuildChannel
        ulong IGuildChannel.GuildId => Guild.Id;

        async Task<IReadOnlyCollection<IInviteMetadata>> IGuildChannel.GetInvitesAsync()
            => await GetInvitesAsync();
        async Task<IInviteMetadata> IGuildChannel.CreateInviteAsync(int? maxAge, int? maxUses, bool isTemporary)
            => await CreateInviteAsync(maxAge, maxUses, isTemporary);

        OverwritePermissions? IGuildChannel.GetPermissionOverwrite(IRole role)
            => GetPermissionOverwrite(role);
        OverwritePermissions? IGuildChannel.GetPermissionOverwrite(IUser user)
            => GetPermissionOverwrite(user);
        async Task IGuildChannel.AddPermissionOverwriteAsync(IRole role, OverwritePermissions permissions)
            => await AddPermissionOverwriteAsync(role, permissions);
        async Task IGuildChannel.AddPermissionOverwriteAsync(IUser user, OverwritePermissions permissions)
            => await AddPermissionOverwriteAsync(user, permissions);
        async Task IGuildChannel.RemovePermissionOverwriteAsync(IRole role)
            => await RemovePermissionOverwriteAsync(role);
        async Task IGuildChannel.RemovePermissionOverwriteAsync(IUser user)
            => await RemovePermissionOverwriteAsync(user);
        
        IAsyncEnumerable<IReadOnlyCollection<IGuildUser>> IGuildChannel.GetUsersAsync(CacheMode mode)
            => ImmutableArray.Create<IReadOnlyCollection<IGuildUser>>(Users).ToAsyncEnumerable();
        Task<IGuildUser> IGuildChannel.GetUserAsync(ulong id, CacheMode mode)
            => Task.FromResult<IGuildUser>(GetUser(id));

        //IChannel
        IAsyncEnumerable<IReadOnlyCollection<IUser>> IChannel.GetUsersAsync(CacheMode mode)
            => ImmutableArray.Create<IReadOnlyCollection<IUser>>(Users).ToAsyncEnumerable(); //Overriden in Text/Voice //TODO: Does this actually override?
        Task<IUser> IChannel.GetUserAsync(ulong id, CacheMode mode)
            => Task.FromResult<IUser>(GetUser(id)); //Overriden in Text/Voice //TODO: Does this actually override?
    }
}