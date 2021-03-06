﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Botwinder.core;
using Botwinder.entities;
using Discord;
using Discord.WebSocket;

using guid = System.UInt64;

namespace Botwinder.modules
{
	public class Logging: IModule
	{
		private BotwinderClient Client;

		private readonly List<guid> RecentlyBannedUserIDs = new List<guid>();
		private readonly List<guid> RecentlyUnbannedUserIDs = new List<guid>();

		private guid RecentVoiceActivityId = 0;
		private int RecentVoiceActivityCount = 0;
		private const int RecentVoiceActivityThreshold = 6;
		private readonly TimeSpan UpdateDelay = TimeSpan.FromMinutes(2);
		private DateTime LastUpdateTime = DateTime.UtcNow;


		public Func<Exception, string, guid, Task> HandleException{ get; set; }

		public List<Command> Init(IBotwinderClient iClient)
		{
			this.Client = iClient as BotwinderClient;

			this.Client.Events.UserJoined += OnUserJoined;
			this.Client.Events.UserLeft += OnUserLeft;
			this.Client.Events.UserVoiceStateUpdated += OnUserVoice;
			this.Client.Events.MessageDeleted += OnMessageDeleted;
			this.Client.Events.MessageUpdated += OnMessageUpdated;
			this.Client.Events.UserBanned += async (user, guild) => {
				Server server;
				if( !this.Client.Servers.ContainsKey(guild.Id) || (server = this.Client.Servers[guild.Id]) == null ||
				    this.RecentlyBannedUserIDs.Contains(user.Id) )
					return;

				await LogBan(server, user.GetUsername(), user.Id, "unknown", "permanently", null);
			};
			this.Client.Events.UserUnbanned += async (user, guild) => {
				Server server;
				if( !this.Client.Servers.ContainsKey(guild.Id) || (server = this.Client.Servers[guild.Id]) == null ||
				    this.RecentlyUnbannedUserIDs.Contains(user.Id) )
					return;

				await LogUnban(server, user.GetUsername(), user.Id, null);
			};

			this.Client.Events.LogBan += LogBan;
			this.Client.Events.LogUnban += LogUnban;
			this.Client.Events.LogKick += LogKick;
			this.Client.Events.LogMute += LogMute;
			this.Client.Events.LogUnmute += LogUnmute;

			this.Client.Events.LogPublicRoleJoin += LogPublicRoleJoin;
			this.Client.Events.LogPublicRoleLeave += LogPublicRoleLeave;
			this.Client.Events.LogPromote += LogPromote;
			this.Client.Events.LogDemote += LogDemote;

			return new List<Command>();
		}

		private async Task OnUserJoined(SocketGuildUser user)
		{
			Server server;
			if( !this.Client.Servers.ContainsKey(user.Guild.Id) ||
			    (server = this.Client.Servers[user.Guild.Id]) == null )
				return;

			SocketTextChannel channel = server.Guild.GetTextChannel(server.Config.ActivityChannelId);
			if( server.Config.LogJoin && channel != null && !string.IsNullOrWhiteSpace(server.Config.LogMessageJoin) )
				await this.Client.SendMessageToChannel(channel,
					string.Format((server.Config.LogTimestampJoin ? $"`{Utils.GetTimestamp()}`: " : "") + server.Config.LogMessageJoin,
						server.Config.LogMentionJoin ? $"<@{user.Id}>" : $"**{user.GetNickname()}**"));
		}

		private async Task OnUserLeft(SocketGuildUser user)
		{
			Server server;
			if( !this.Client.Servers.ContainsKey(user.Guild.Id) ||
			    (server = this.Client.Servers[user.Guild.Id]) == null )
				return;

			SocketTextChannel channel = server.Guild.GetTextChannel(server.Config.ActivityChannelId);
			if( server.Config.LogLeave && channel != null && !string.IsNullOrWhiteSpace(server.Config.LogMessageLeave) )
				await this.Client.SendMessageToChannel(channel,
					string.Format((server.Config.LogTimestampLeave ? $"`{Utils.GetTimestamp()}`: " : "") + server.Config.LogMessageLeave,
						server.Config.LogMentionLeave ? $"<@{user.Id}>" : $"**{user.GetNickname()}**"));
		}

		private async Task OnUserVoice(SocketUser u, SocketVoiceState originalState, SocketVoiceState newState)
		{
			Server server;
			guid id = newState.VoiceChannel?.Guild.Id ?? originalState.VoiceChannel?.Guild.Id ?? 0;
			if( id == 0 ||
			    !this.Client.Servers.ContainsKey(id) ||
			    (server = this.Client.Servers[id]) == null ||
			    !(u is SocketGuildUser user) ||
			    originalState.VoiceChannel == newState.VoiceChannel )
				return;

			SocketTextChannel channel;
			if( server.Config.VoiceChannelId != 0 &&
			    (channel = server.Guild.GetTextChannel(server.Config.VoiceChannelId)) != null )
			{
				if( originalState.VoiceChannel == null && newState.VoiceChannel == null )
					throw new NotImplementedException("Logging.VoiceState.VoiceChannel(s) are null.");

				int change = originalState.VoiceChannel == null ? 1 :
					newState.VoiceChannel == null ? -1 : 0;

				string message = "";
				switch(change)
				{
					case -1:
						message = $"`{Utils.GetTimestamp()}`:  **{user.GetNickname()}** left the `{originalState.VoiceChannel.Name}` voice channel.";
						break;
					case 1:
						message = $"`{Utils.GetTimestamp()}`:  **{user.GetNickname()}** joined the `{newState.VoiceChannel.Name}` voice channel.";
						break;
					case 0:
						message = $"`{Utils.GetTimestamp()}`:  **{user.GetNickname()}** switched from the `{originalState.VoiceChannel.Name}` voice channel, to the `{newState.VoiceChannel.Name}` voice channel.";
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}

				if( this.RecentVoiceActivityId == user.Id && ++this.RecentVoiceActivityCount >= RecentVoiceActivityThreshold )
				{
					if( this.RecentVoiceActivityCount == RecentVoiceActivityThreshold )
						message = $"<@{user.Id}> stop spamming me or face the consequences.";
					if( this.RecentVoiceActivityCount == RecentVoiceActivityThreshold + 1 )
					{
						message = "";
						try
						{
							await user.SendMessageSafe(string.Format("You're one voice channel switch away from getting banned on the `{0}` server. Do not spam-switch please :<", server.Guild.Name));
						}
						catch(Exception) { }
					}
					if( this.RecentVoiceActivityCount == RecentVoiceActivityThreshold + 2 )
					{
						message = "";
						try
						{
							UserData userData = ServerContext.Create(this.Client.DbConnectionString).GetOrAddUser(server.Id, user.Id);
							await this.Client.Events.BanUser(server, userData, TimeSpan.FromHours(1), "Excessive spamming - voice chat switching (by Botwinder)", server.Guild.CurrentUser, false, true);
						}
						catch(Exception) { }
					}
				}

				if( this.RecentVoiceActivityId != user.Id )
				{
					this.RecentVoiceActivityId = user.Id;
					this.RecentVoiceActivityCount = 1;
				}

				if( string.IsNullOrEmpty(message) )
					await channel.SendMessageSafe(message);
			}
		}

		private async Task OnMessageDeleted(SocketMessage message, ISocketMessageChannel c)
		{
			if( !(c is SocketTextChannel channel) )
				return;

			Server server;
			if( !this.Client.Servers.ContainsKey(channel.Guild.Id) ||
			    (server = this.Client.Servers[channel.Guild.Id]) == null ||
			    server.Config.IgnoreBots && message.Author.IsBot ||
			    !(message.Author is SocketGuildUser user) )
				return;

			SocketTextChannel logChannel;
			if( server.Config.LogDeletedMessages && (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) != null && !(
			     (this.Client.ClearedMessageIDs.ContainsKey(server.Id) && this.Client.ClearedMessageIDs[server.Id].Contains(message.Id)) ||
			     server.IgnoredChannels.Contains(channel.Id) ||
			     server.Roles.Where(r => r.Value.LoggingIgnored).Any(r => user.Roles.Any(role => role.Id == r.Value.RoleId)) ) )
			{
				StringBuilder attachment = new StringBuilder();
				if( message.Attachments != null && message.Attachments.Any() )
					foreach(Attachment a in message.Attachments)
						if( !string.IsNullOrWhiteSpace(a.Url) )
							attachment.AppendLine(a.Url);

				await logChannel.SendMessageSafe(
					GetLogMessage("Message Deleted", "#" + channel.Name,
						message.Author.GetUsername(), message.Author.Id.ToString(),
						message.Id,
						"Message", message.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here"),
						message.Attachments.Any() ? "Files" : "", attachment.ToString()));
			}
		}

		private async Task OnMessageUpdated(SocketMessage originalMessage, SocketMessage updatedMessage, ISocketMessageChannel c)
		{
			if( !(c is SocketTextChannel channel) )
				return;

			Server server;
			if( !this.Client.Servers.ContainsKey(channel.Guild.Id) ||
			    (server = this.Client.Servers[channel.Guild.Id]) == null ||
			    server.Config.IgnoreBots && updatedMessage.Author.IsBot ||
			    !(updatedMessage.Author is SocketGuildUser user) )
				return;

			SocketTextChannel logChannel;
			if( server.Config.LogEditedMessages && (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId))!= null && !(
				 server.IgnoredChannels.Contains(channel.Id) ||
				 server.Roles.Where(r => r.Value.LoggingIgnored).Any(r => user.Roles.Any(role => role.Id == r.Value.RoleId)) ) )
			{
				await logChannel.SendMessageSafe(
					GetLogMessage("Message Edited", "#" + channel.Name,
						updatedMessage.Author.GetUsername(), updatedMessage.Author.Id.ToString(),
						updatedMessage.Id,
						"Before", originalMessage.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here"),
						"After", updatedMessage.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here")));
			}
		}


		private async Task LogBan(Server server, string userName, guid userId, string reason, string duration, SocketGuildUser issuedBy)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
				return;

			this.RecentlyBannedUserIDs.Add(userId); //Don't trigger the on-event log message as well as this custom one.

			await logChannel.SendMessageSafe(
				GetLogMessage("User Banned " + duration, (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
					userName ?? "", userId.ToString(),
					Utils.GetTimestamp(),
					"Reason", reason));
		}

		private async Task LogUnban(Server server, string userName, guid userId, SocketGuildUser issuedBy)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
				return;

			this.RecentlyUnbannedUserIDs.Add(userId); //Don't trigger the on-event log message as well as this custom one.

			await logChannel.SendMessageSafe(
				GetLogMessage("User Unbanned", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
					userName ?? "", userId.ToString(),
					Utils.GetTimestamp()));
		}

		private async Task LogKick(Server server, string userName, guid userId, string reason, SocketGuildUser issuedBy)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
				return;

			await logChannel.SendMessageSafe(
				GetLogMessage("User Kicked", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
					userName ?? "", userId.ToString(),
					Utils.GetTimestamp(),
					"Reason", reason));
		}

		private async Task LogMute(Server server, SocketGuildUser user, string duration, SocketGuildUser issuedBy)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
				return;

			await logChannel.SendMessageSafe(
				GetLogMessage("User Muted " + duration, (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
					user.GetUsername(), user.Id.ToString(),
					Utils.GetTimestamp()));
		}

		private async Task LogUnmute(Server server, SocketGuildUser user, SocketGuildUser issuedBy)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
				return;

			await logChannel.SendMessageSafe(
				GetLogMessage("User Unmuted ", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
					user.GetUsername(), user.Id.ToString(),
					Utils.GetTimestamp()));
		}


		private async Task LogPublicRoleJoin(Server server, SocketGuildUser user, string roleName)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
				return;

			await logChannel.SendMessageSafe($"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** joined the `{roleName}` public role.");
		}

		private async Task LogPublicRoleLeave(Server server, SocketGuildUser user, string roleName)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
				return;

			await logChannel.SendMessageSafe($"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** left the `{roleName}` public role.");
		}

		private async Task LogPromote(Server server, SocketGuildUser user, string roleName, SocketGuildUser issuedBy)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
				return;

			await logChannel.SendMessageSafe($"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** was promoted to the `{roleName}` member role by __{issuedBy.GetUsername()}__");
		}

		private async Task LogDemote(Server server, SocketGuildUser user, string roleName, SocketGuildUser issuedBy)
		{
			SocketTextChannel logChannel;
			if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
				return;

			await logChannel.SendMessageSafe($"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** was demoted from the `{roleName}` member role by __{issuedBy.GetUsername()}__");
		}


		public static string GetLogMessage(string titleRed, string infoGreen, string nameGold, string idGreen,
			guid timestampId, string tag1 = "", string msg1 = "", string tag2 = "", string msg2 = "")
			=> GetLogMessage(titleRed, infoGreen, nameGold, idGreen,
				Utils.GetTimestamp(Utils.GetTimeFromId(timestampId)),
				tag1, msg1, tag2, msg2);

		public static string GetLogMessage(string titleRed, string infoGreen, string nameGold, string idGreen,
			string timestamp, string tag1 = "", string msg1 = "", string tag2 = "", string msg2 = "")
		{
			msg1 = msg1.Replace('`', '\'');
			msg2 = msg2.Replace('`', '\'');
			int length = titleRed.Length + infoGreen.Length + nameGold.Length + idGreen.Length + msg1.Length + msg2.Length + timestamp.Length + 100;
			int messageLimit = 1500;
			while( length >= GlobalConfig.MessageCharacterLimit )
			{
				msg1 = msg1.Substring(0, Math.Min(messageLimit, msg1.Length)) + "**...**";
				if( !string.IsNullOrWhiteSpace(msg2) )
					msg2 = msg2.Substring(0, Math.Min(messageLimit, msg2.Length)) + "**...**";

				length = titleRed.Length + infoGreen.Length + nameGold.Length + idGreen.Length + msg1.Length + msg2.Length + timestamp.Length + 100;
				messageLimit -= 100;
			}

			string message = "";
			string tag = "";
			if( string.IsNullOrWhiteSpace(tag1) && !string.IsNullOrWhiteSpace(msg1) )
				message += msg1;
			else if( !string.IsNullOrWhiteSpace(tag1) && !string.IsNullOrWhiteSpace(msg1) )
			{
				tag = "<" + tag1;
				while( tag.Length < 9 )
					tag += " ";
				message += tag + "> " + msg1;
			}

			if( string.IsNullOrWhiteSpace(tag2) && !string.IsNullOrWhiteSpace(msg2) )
				message += "\n" + msg2;
			else if( !string.IsNullOrWhiteSpace(tag2) && !string.IsNullOrWhiteSpace(msg2) )
			{
				tag = "<" + tag2;
				while( tag.Length < 9 )
					tag += " ";
				message += "\n" + tag + "> " + msg2;
			}

			return string.Format("```md\n# {0}\n[{1}]({2})\n< {3} ={4}>\n{5}\n```", titleRed, timestamp, infoGreen, nameGold, idGreen, message);
		}

		public Task Update(IBotwinderClient iClient)
		{
			if( this.LastUpdateTime + this.UpdateDelay > DateTime.UtcNow )
				return Task.CompletedTask;

			this.LastUpdateTime = DateTime.UtcNow;

			this.RecentVoiceActivityId = 0;
			this.RecentlyBannedUserIDs.Clear();
			this.RecentlyUnbannedUserIDs.Clear();

			return Task.CompletedTask;
		}
	}
}
