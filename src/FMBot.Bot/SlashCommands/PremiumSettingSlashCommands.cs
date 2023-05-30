using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Discord.Interactions;
using FMBot.Bot.Attributes;
using FMBot.Bot.Builders;
using FMBot.Bot.Extensions;
using FMBot.Bot.Models;
using FMBot.Bot.Resources;
using FMBot.Bot.Services;
using FMBot.Bot.Services.Guild;
using FMBot.Domain.Models;

namespace FMBot.Bot.SlashCommands;

public class PremiumSettingSlashCommands : InteractionModuleBase
{
    private readonly UserService _userService;
    private readonly GuildService _guildService;

    private readonly PremiumSettingBuilder _premiumSettingBuilder;
    private readonly GuildSettingBuilder _guildSettingBuilder;

    public PremiumSettingSlashCommands(UserService userService,
        GuildService guildService,
        PremiumSettingBuilder premiumSettingBuilder,
        GuildSettingBuilder guildSettingBuilder)
    {
        this._userService = userService;
        this._guildService = guildService;
        this._premiumSettingBuilder = premiumSettingBuilder;
        this._guildSettingBuilder = guildSettingBuilder;
    }

    [ComponentInteraction(InteractionConstants.SetAllowedRoleMenu)]
    [ServerStaffOnly]
    public async Task SetGuildAllowedRoles(string[] inputs)
    {
        if (!await this._guildSettingBuilder.UserIsAllowed(this.Context))
        {
            await GuildSettingBuilder.UserNotAllowedResponse(this.Context);
            this.Context.LogCommandUsed(CommandResponse.NoPermission);
            return;
        }

        if (inputs != null)
        {
            var roleIds = new List<ulong>();
            foreach (var input in inputs)
            {
                var roleId = ulong.Parse(input);
                roleIds.Add(roleId);
            }

            await this._guildService.ChangeGuildAllowedRoles(this.Context.Guild, roleIds.ToArray());
        }

        var response = await this._premiumSettingBuilder.AllowedRoles(new ContextModel(this.Context), this.Context.User);

        await this.DeferAsync();

        await this.Context.UpdateInteractionEmbed(response);
    }

    [ComponentInteraction(InteractionConstants.SetBlockedRoleMenu)]
    [ServerStaffOnly]
    public async Task SetGuildBlockedRoles(string[] inputs)
    {
        if (!await this._guildSettingBuilder.UserIsAllowed(this.Context))
        {
            await GuildSettingBuilder.UserNotAllowedResponse(this.Context);
            this.Context.LogCommandUsed(CommandResponse.NoPermission);
            return;
        }

        if (inputs != null)
        {
            var roleIds = new List<ulong>();
            foreach (var input in inputs)
            {
                var roleId = ulong.Parse(input);
                roleIds.Add(roleId);
            }

            await this._guildService.ChangeGuildBlockedRoles(this.Context.Guild, roleIds.ToArray());
        }

        var response = await this._premiumSettingBuilder.BlockedRoles(new ContextModel(this.Context), this.Context.User);

        await this.DeferAsync();

        await this.Context.UpdateInteractionEmbed(response);
    }

    [ComponentInteraction(InteractionConstants.SetBotManagementRoleMenu)]
    [ServerStaffOnly]
    public async Task SetBotManagementRoles(string[] inputs)
    {
        if (!await this._guildSettingBuilder.UserIsAllowed(this.Context, managersAllowed: false))
        {
            await GuildSettingBuilder.UserNotAllowedResponse(this.Context, managersAllowed: false);
            this.Context.LogCommandUsed(CommandResponse.NoPermission);
            return;
        }

        if (inputs != null)
        {
            var roleIds = new List<ulong>();
            foreach (var input in inputs)
            {
                var roleId = ulong.Parse(input);
                roleIds.Add(roleId);
            }

            await this._guildService.ChangeGuildBotManagementRoles(this.Context.Guild, roleIds.ToArray());
        }

        var response = await this._premiumSettingBuilder.BotManagementRoles(new ContextModel(this.Context), this.Context.User);

        await this.DeferAsync();

        await this.Context.UpdateInteractionEmbed(response);
    }
}
