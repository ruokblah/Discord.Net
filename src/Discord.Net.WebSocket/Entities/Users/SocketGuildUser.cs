﻿using Discord.API.Rest;
using Discord.Rest;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Model = Discord.API.GuildMember;
using PresenceModel = Discord.API.Presence;

namespace Discord.WebSocket
{
    public class SocketGuildUser : SocketUser, IGuildUser
    {
        private long? _joinedAtTicks;
        private ImmutableArray<ulong> _roleIds;

        internal override SocketGlobalUser GlobalUser { get; }
        public SocketGuild Guild { get; }
        public string Nickname { get; private set; }

        public override bool IsBot { get { return GlobalUser.IsBot; } internal set { GlobalUser.IsBot = value; } }
        public override string Username { get { return GlobalUser.Username; } internal set { GlobalUser.Username = value; } }
        public override ushort DiscriminatorValue { get { return GlobalUser.DiscriminatorValue; } internal set { GlobalUser.DiscriminatorValue = value; } }
        public override string AvatarId { get { return GlobalUser.AvatarId; } internal set { GlobalUser.AvatarId = value; } }
        internal override SocketPresence Presence { get { return GlobalUser.Presence; } set { GlobalUser.Presence = value; } }
        public GuildPermissions GuildPermissions => new GuildPermissions(Permissions.ResolveGuild(Guild, this));
        public IReadOnlyCollection<ulong> RoleIds => _roleIds;

        public SocketVoiceState? VoiceState => Guild.GetVoiceState(Id);
        public bool IsSelfDeafened => VoiceState?.IsSelfDeafened ?? false;
        public bool IsSelfMuted => VoiceState?.IsSelfMuted ?? false;
        public bool IsSuppressed => VoiceState?.IsSuppressed ?? false;
        public SocketVoiceChannel VoiceChannel => VoiceState?.VoiceChannel;
        public bool IsDeafened => VoiceState?.IsDeafened ?? false;
        public bool IsMuted => VoiceState?.IsMuted ?? false;
        public string VoiceSessionId => VoiceState?.VoiceSessionId ?? "";

        public DateTimeOffset? JoinedAt => DateTimeUtils.FromTicks(_joinedAtTicks);

        internal SocketGuildUser(SocketGuild guild, SocketGlobalUser globalUser)
            : base(guild.Discord, globalUser.Id)
        {
            Guild = guild;
            GlobalUser = globalUser;
        }
        internal static SocketGuildUser Create(SocketGuild guild, ClientState state, Model model)
        {
            var entity = new SocketGuildUser(guild, guild.Discord.GetOrCreateUser(state, model.User));
            entity.Update(state, model);
            return entity;
        }
        internal static SocketGuildUser Create(SocketGuild guild, ClientState state, PresenceModel model)
        {
            var entity = new SocketGuildUser(guild, guild.Discord.GetOrCreateUser(state, model.User));
            entity.Update(state, model);
            return entity;
        }
        internal void Update(ClientState state, Model model)
        {
            base.Update(state, model.User);
            _joinedAtTicks = model.JoinedAt.UtcTicks;
            if (model.Nick.IsSpecified)
                Nickname = model.Nick.Value;
            UpdateRoles(model.Roles);
        }
        internal override void Update(ClientState state, PresenceModel model)
        {
            base.Update(state, model);
            if (model.Roles.IsSpecified)
                UpdateRoles(model.Roles.Value);
            if (model.Nick.IsSpecified)
                Nickname = model.Nick.Value;
        }
        private void UpdateRoles(ulong[] roleIds)
        {
            var roles = ImmutableArray.CreateBuilder<ulong>(roleIds.Length + 1);
            roles.Add(Guild.Id);
            for (int i = 0; i < roleIds.Length; i++)
                roles.Add(roleIds[i]);
            _roleIds = roles.ToImmutable();
        }
        
        public Task ModifyAsync(Action<ModifyGuildMemberParams> func)
            => UserHelper.ModifyAsync(this, Discord, func);
        public Task KickAsync()
            => UserHelper.KickAsync(this, Discord);

        public ChannelPermissions GetPermissions(IGuildChannel channel)
        {
            throw new NotImplementedException(); //TODO: Impl
        }

        internal new SocketGuildUser Clone() => MemberwiseClone() as SocketGuildUser;

        //IGuildUser
        ulong IGuildUser.GuildId => Guild.Id;
        IReadOnlyCollection<ulong> IGuildUser.RoleIds => RoleIds;

        //IUser
        Task<IDMChannel> IUser.GetDMChannelAsync(CacheMode mode) 
            => Task.FromResult<IDMChannel>(GlobalUser.DMChannel);

        //IVoiceState
        IVoiceChannel IVoiceState.VoiceChannel => VoiceChannel;
    }
}