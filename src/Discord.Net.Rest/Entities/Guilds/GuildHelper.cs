﻿using Discord.API.Rest;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using EmbedModel = Discord.API.GuildEmbed;
using Model = Discord.API.Guild;
using RoleModel = Discord.API.Role;

namespace Discord.Rest
{
    internal static class GuildHelper
    {
        //General
        public static async Task<Model> ModifyAsync(IGuild guild, BaseDiscordClient client,
            Action<ModifyGuildParams> func)
        {
            if (func == null) throw new NullReferenceException(nameof(func));

            var args = new ModifyGuildParams();
            func(args);

            if (args.Splash.IsSpecified && guild.SplashId != null)
                args.Splash = new API.Image(guild.SplashId);
            if (args.Icon.IsSpecified && guild.IconId != null)
                args.Icon = new API.Image(guild.IconId);

            return await client.ApiClient.ModifyGuildAsync(guild.Id, args).ConfigureAwait(false);
        }
        public static async Task<EmbedModel> ModifyEmbedAsync(IGuild guild, BaseDiscordClient client,
            Action<ModifyGuildEmbedParams> func)
        {
            if (func == null) throw new NullReferenceException(nameof(func));

            var args = new ModifyGuildEmbedParams();
            func(args);
            return await client.ApiClient.ModifyGuildEmbedAsync(guild.Id, args).ConfigureAwait(false);
        }
        public static async Task ModifyChannelsAsync(IGuild guild, BaseDiscordClient client,
            IEnumerable<ModifyGuildChannelsParams> args)
        {
            await client.ApiClient.ModifyGuildChannelsAsync(guild.Id, args).ConfigureAwait(false);
        }
        public static async Task<IReadOnlyCollection<RoleModel>> ModifyRolesAsync(IGuild guild, BaseDiscordClient client,
            IEnumerable<ModifyGuildRolesParams> args)
        {
            return await client.ApiClient.ModifyGuildRolesAsync(guild.Id, args).ConfigureAwait(false);
        }
        public static async Task LeaveAsync(IGuild guild, BaseDiscordClient client)
        {
            await client.ApiClient.LeaveGuildAsync(guild.Id).ConfigureAwait(false);
        }
        public static async Task DeleteAsync(IGuild guild, BaseDiscordClient client)
        {
            await client.ApiClient.DeleteGuildAsync(guild.Id).ConfigureAwait(false);
        }

        //Bans
        public static async Task<IReadOnlyCollection<RestBan>> GetBansAsync(IGuild guild, BaseDiscordClient client)
        {
            var models = await client.ApiClient.GetGuildBansAsync(guild.Id);
            return models.Select(x => RestBan.Create(client, x)).ToImmutableArray();
        }
        
        public static async Task AddBanAsync(IGuild guild, BaseDiscordClient client, 
            ulong userId, int pruneDays)
        {
            var args = new CreateGuildBanParams { DeleteMessageDays = pruneDays };
            await client.ApiClient.CreateGuildBanAsync(guild.Id, userId, args);
        }        
        public static async Task RemoveBanAsync(IGuild guild, BaseDiscordClient client, 
            ulong userId)
        {
            await client.ApiClient.RemoveGuildBanAsync(guild.Id, userId);
        }

        //Channels
        public static async Task<RestGuildChannel> GetChannelAsync(IGuild guild, BaseDiscordClient client,
            ulong id)
        {
            var model = await client.ApiClient.GetChannelAsync(guild.Id, id).ConfigureAwait(false);
            if (model != null)
                return RestGuildChannel.Create(client, guild, model);
            return null;
        }
        public static async Task<IReadOnlyCollection<RestGuildChannel>> GetChannelsAsync(IGuild guild, BaseDiscordClient client)
        {
            var models = await client.ApiClient.GetGuildChannelsAsync(guild.Id).ConfigureAwait(false);
            return models.Select(x => RestGuildChannel.Create(client, guild, x)).ToImmutableArray();
        }
        public static async Task<RestTextChannel> CreateTextChannelAsync(IGuild guild, BaseDiscordClient client,
            string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            var args = new CreateGuildChannelParams(name, ChannelType.Text);
            var model = await client.ApiClient.CreateGuildChannelAsync(guild.Id, args).ConfigureAwait(false);
            return RestTextChannel.Create(client, guild, model);
        }
        public static async Task<RestVoiceChannel> CreateVoiceChannelAsync(IGuild guild, BaseDiscordClient client,
            string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            var args = new CreateGuildChannelParams(name, ChannelType.Voice);
            var model = await client.ApiClient.CreateGuildChannelAsync(guild.Id, args).ConfigureAwait(false);
            return RestVoiceChannel.Create(client, guild, model);
        }

        //Integrations
        public static async Task<IReadOnlyCollection<RestGuildIntegration>> GetIntegrationsAsync(IGuild guild, BaseDiscordClient client)
        {
            var models = await client.ApiClient.GetGuildIntegrationsAsync(guild.Id).ConfigureAwait(false);
            return models.Select(x => RestGuildIntegration.Create(client, x)).ToImmutableArray();
        }
        public static async Task<RestGuildIntegration> CreateIntegrationAsync(IGuild guild, BaseDiscordClient client,
            ulong id, string type)
        {
            var args = new CreateGuildIntegrationParams(id, type);
            var model = await client.ApiClient.CreateGuildIntegrationAsync(guild.Id, args).ConfigureAwait(false);
            return RestGuildIntegration.Create(client, model);
        }

        //Invites
        public static async Task<IReadOnlyCollection<RestInviteMetadata>> GetInvitesAsync(IGuild guild, BaseDiscordClient client)
        {
            var models = await client.ApiClient.GetGuildInvitesAsync(guild.Id).ConfigureAwait(false);
            return models.Select(x => RestInviteMetadata.Create(client, x)).ToImmutableArray();
        }

        //Roles
        public static async Task<RestRole> CreateRoleAsync(IGuild guild, BaseDiscordClient client,
            string name, GuildPermissions? permissions = null, Color? color = null, bool isHoisted = false)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            var model = await client.ApiClient.CreateGuildRoleAsync(guild.Id).ConfigureAwait(false);
            var role = RestRole.Create(client, model);

            await role.ModifyAsync(x =>
            {
                x.Name = name;
                x.Permissions = (permissions ?? role.Permissions).RawValue;
                x.Color = (color ?? Color.Default).RawValue;
                x.Hoist = isHoisted;
            }).ConfigureAwait(false);

            return role;
        }

        //Users
        public static async Task<RestGuildUser> GetUserAsync(IGuild guild, BaseDiscordClient client,
            ulong id)
        {
            var model = await client.ApiClient.GetGuildMemberAsync(guild.Id, id).ConfigureAwait(false);
            if (model != null)
                return RestGuildUser.Create(client, guild, model);
            return null;
        }
        public static async Task<RestGuildUser> GetCurrentUserAsync(IGuild guild, BaseDiscordClient client)
        {
            return await GetUserAsync(guild, client, client.CurrentUser.Id).ConfigureAwait(false);
        }
        public static async Task<IReadOnlyCollection<RestGuildUser>> GetUsersAsync(IGuild guild, BaseDiscordClient client)
        {
            var args = new GetGuildMembersParams();
            var models = await client.ApiClient.GetGuildMembersAsync(guild.Id, args).ConfigureAwait(false);
            return models.Select(x => RestGuildUser.Create(client, guild, x)).ToImmutableArray();
        }
        public static async Task<int> PruneUsersAsync(IGuild guild, BaseDiscordClient client,
            int days = 30, bool simulate = false)
        {
            var args = new GuildPruneParams(days);
            GetGuildPruneCountResponse model;
            if (simulate)
                model = await client.ApiClient.GetGuildPruneCountAsync(guild.Id, args).ConfigureAwait(false);
            else
                model = await client.ApiClient.BeginGuildPruneAsync(guild.Id, args).ConfigureAwait(false);
            return model.Pruned;
        }
    }
}