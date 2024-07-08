using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Discord;
using FMBot.Bot.Models;
using FMBot.Bot.Resources;
using System.Text;
using System.Threading;
using FMBot.Domain.Models;
using System.Threading.Tasks;
using FMBot.Bot.Extensions;
using FMBot.Bot.Services;
using Discord.Commands;
using FMBot.Domain;
using FMBot.Domain.Interfaces;
using Serilog;
using FMBot.Persistence.Domain.Models;
using SkiaSharp;

namespace FMBot.Bot.Builders;

public class GameBuilders
{
    private readonly UserService _userService;
    private readonly GameService _gameService;
    private readonly ArtistsService _artistsService;
    private readonly CountryService _countryService;
    private readonly AlbumService _albumService;
    private readonly IDataSourceFactory _dataSourceFactory;

    public GameBuilders(UserService userService, GameService gameService, ArtistsService artistsService, CountryService countryService, AlbumService albumService, IDataSourceFactory dataSourceFactory)
    {
        this._userService = userService;
        this._gameService = gameService;
        this._artistsService = artistsService;
        this._countryService = countryService;
        this._albumService = albumService;
        this._dataSourceFactory = dataSourceFactory;
    }

    public async Task<ResponseModel> StartJumbleFirstWins(ContextModel context, int userId,
        CancellationTokenSource cancellationTokenSource)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.Embed,
        };

        var existingGame = await this._gameService.GetJumbleSessionForChannelId(context.DiscordChannel.Id);
        if (existingGame != null && !existingGame.DateEnded.HasValue)
        {
            if (existingGame.DateStarted <= DateTime.UtcNow.AddSeconds(-(GameService.JumbleSecondsToGuess + 10)))
            {
                await this._gameService.JumbleEndSession(existingGame);
            }
            else
            {
                response.CommandResponse = CommandResponse.Cooldown;
                return response;
            }
        }

        var recentJumbles = await this._gameService.GetJumbleSessionsCountToday(context.ContextUser.UserId);
        var jumblesPlayedToday = recentJumbles.Count(c => c.DateStarted.Date == DateTime.Today);
        const int jumbleLimit = 30;
        if (!SupporterService.IsSupporter(context.ContextUser.UserType) && jumblesPlayedToday > jumbleLimit)
        {
            response.Embed.WithColor(DiscordConstants.InformationColorBlue);
            response.Embed.WithDescription($"You've used up all your {jumbleLimit} jumbles of today. [Get supporter]({Constants.GetSupporterDiscordLink}) to play unlimited jumble games and much more.");
            response.Components = new ComponentBuilder()
                .WithButton(Constants.GetSupporterButton, style: ButtonStyle.Link, url: Constants.GetSupporterDiscordLink);
            response.CommandResponse = CommandResponse.SupporterRequired;
            return response;
        }

        var topArtists = await this._artistsService.GetUserAllTimeTopArtists(userId, true);

        if (topArtists.Count(c => c.UserPlaycount > 30) <= 6)
        {
            response.Embed.WithColor(DiscordConstants.WarningColorOrange);
            response.Embed.WithDescription($"Sorry, you haven't listened to enough artists yet to use this command. Please scrobble more music and try again later.");
            response.CommandResponse = CommandResponse.NoScrobbles;
            return response;
        }

        var artist = GameService.PickArtistForJumble(topArtists, recentJumbles);

        if (artist.artist == null)
        {
            response.Embed.WithDescription($"You've played all jumbles that are available for you today. Come back tomorrow or scrobble more music to play again.");
            response.CommandResponse = CommandResponse.NotFound;
            return response;
        }

        var databaseArtist = await this._artistsService.GetArtistFromDatabase(artist.artist);
        if (databaseArtist == null)
        {
            // Pick someone else and hope for the best
            artist = GameService.PickArtistForJumble(topArtists);
            databaseArtist = await this._artistsService.GetArtistFromDatabase(artist.artist);
        }

        var game = await this._gameService.StartJumbleGame(userId, context, JumbleType.JumbleFirstWins, artist.artist, cancellationTokenSource);

        CountryInfo artistCountry = null;
        if (databaseArtist?.CountryCode != null)
        {
            artistCountry = this._countryService.GetValidCountry(databaseArtist.CountryCode);
        }

        var hints = this._gameService.GetJumbleArtistHints(databaseArtist, artist.userPlaycount, artistCountry);
        await this._gameService.JumbleStoreShowedHints(game, hints);

        BuildJumbleEmbed(response.Embed, game.JumbledArtist, game.Hints);
        response.Components = BuildJumbleComponents(game.JumbleSessionId, game.Hints);
        response.GameSessionId = game.JumbleSessionId;

        return response;
    }

    private static void BuildJumbleEmbed(EmbedBuilder embed, string jumbledArtist, List<JumbleSessionHint> hints, bool canBeAnswered = true, JumbleType jumbleType = JumbleType.JumbleFirstWins)
    {
        var hintsShown = hints.Count(w => w.HintShown);
        var hintString = GameService.HintsToString(hints, hintsShown);

        embed.WithColor(DiscordConstants.InformationColorBlue);

        if (jumbleType == JumbleType.JumbleFirstWins)
        {
            embed.WithAuthor("Guess the artist - Jumble");
            embed.WithDescription($"### `{jumbledArtist}`");
        }
        else
        {
            embed.WithAuthor("Guess the album - Pixelation");
        }

        var hintTitle = "Hints";
        if (hintsShown > 3)
        {
            hintTitle = $"Hints + {hintsShown - 3} extra {StringExtensions.GetHintsString(hintsShown - 3)}";
        }
        embed.AddField(hintTitle, hintString);

        if (canBeAnswered)
        {
            embed.AddField("Add answer",
                jumbleType == JumbleType.JumbleFirstWins
                    ? $"Type your answer within {GameService.JumbleSecondsToGuess} seconds to make a guess"
                    : $"Type your answer within {GameService.PixelationSecondsToGuess} seconds to make a guess");
        }
    }

    public async Task<ResponseModel> GetJumbleUserStats(ContextModel context, UserSettingsModel userSettings)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.Embed,
        };

        response.Embed.WithAuthor($"Jumble stats - {userSettings.DisplayName}");

        var userStats =
            await this._gameService.GetJumbleUserStats(userSettings.UserId, userSettings.DiscordUserId);

        if (userStats == null)
        {
            response.Embed.WithDescription(userSettings.DifferentUser
                ? "No stats available for this user."
                : "No stats available for you yet.");
            response.CommandResponse = CommandResponse.NoScrobbles;
            return response;
        }

        var gameStats = new StringBuilder();
        gameStats.AppendLine($"- **{userStats.TotalGamesPlayed}** total games played");
        gameStats.AppendLine($"- **{userStats.GamesStarted}** games started");
        gameStats.AppendLine($"- **{userStats.GamesAnswered}** games answered");
        gameStats.AppendLine($"- **{userStats.GamesWon}** games won");
        gameStats.AppendLine($"- **{decimal.Round(userStats.AvgHintsShown, 1)}** average hints shown");
        response.Embed.AddField("Games", gameStats.ToString());

        var answerStats = new StringBuilder();
        answerStats.AppendLine($"- **{userStats.TotalAnswers}** total answers");
        answerStats.AppendLine($"- **{decimal.Round(userStats.AvgAnsweringTime, 1)}s** average answer time");
        answerStats.AppendLine($"- **{decimal.Round(userStats.AvgCorrectAnsweringTime, 1)}s** average correct answer time");
        answerStats.AppendLine($"- **{decimal.Round(userStats.AvgAttemptsUntilCorrect, 1)}** average attempts until correct");
        answerStats.AppendLine($"- **{decimal.Round(userStats.Winrate, 1)}%** winrate");
        response.Embed.AddField("Answers", answerStats.ToString());

        return response;
    }

    private static ComponentBuilder BuildJumbleComponents(int gameId, List<JumbleSessionHint> hints)
    {
        var addHintDisabled = hints.Count(c => c.HintShown) == hints.Count;

        return new ComponentBuilder()
            .WithButton("Add hint", $"{InteractionConstants.Game.AddJumbleHint}-{gameId}", ButtonStyle.Secondary, disabled: addHintDisabled)
            .WithButton("Reshuffle", $"{InteractionConstants.Game.JumbleReshuffle}-{gameId}", ButtonStyle.Secondary)
            .WithButton("Give up", $"{InteractionConstants.Game.JumbleGiveUp}-{gameId}", ButtonStyle.Secondary);
    }

    public async Task<ResponseModel> JumbleAddHint(ContextModel context, int parsedGameId)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.Embed,
        };

        var currentGame = await this._gameService.GetJumbleSessionForSessionId(parsedGameId);
        if (currentGame == null || currentGame.DateEnded.HasValue)
        {
            return response;
        }

        GameService.HintsToString(currentGame.Hints, currentGame.Hints.Count(w => w.HintShown) + 1);
        await this._gameService.JumbleStoreShowedHints(currentGame, currentGame.Hints);

        BuildJumbleEmbed(response.Embed, currentGame.JumbledArtist, currentGame.Hints);
        response.Components = BuildJumbleComponents(currentGame.JumbleSessionId, currentGame.Hints);

        return response;
    }

    public async Task<ResponseModel> JumbleReshuffle(ContextModel context, int parsedGameId)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.Embed,
        };

        var currentGame = await this._gameService.GetJumbleSessionForSessionId(parsedGameId);
        if (currentGame == null || currentGame.DateEnded.HasValue)
        {
            return response;
        }

        await this._gameService.JumbleReshuffleArtist(currentGame);

        BuildJumbleEmbed(response.Embed, currentGame.JumbledArtist, currentGame.Hints);
        response.Components = BuildJumbleComponents(currentGame.JumbleSessionId, currentGame.Hints);

        return response;
    }

    public async Task<ResponseModel> JumbleGiveUp(ContextModel context, int parsedGameId)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.Embed,
        };

        var currentGame = await this._gameService.GetJumbleSessionForSessionId(parsedGameId);
        if (currentGame == null || currentGame.DateEnded.HasValue)
        {
            return response;
        }

        if (currentGame.StarterUserId != context.ContextUser.UserId)
        {
            response.Embed.WithDescription("You can't give up on someone else their game.");
            response.Embed.WithColor(DiscordConstants.WarningColorOrange);
            response.CommandResponse = CommandResponse.NoPermission;
            return response;
        }

        await this._gameService.JumbleEndSession(currentGame);
        await this._gameService.CancelToken(context.DiscordChannel.Id);

        BuildJumbleEmbed(response.Embed, currentGame.JumbledArtist, currentGame.Hints, false);

        var userTitle = await UserService.GetNameAsync(context.DiscordGuild, context.DiscordUser);

        response.Embed.AddField($"{userTitle} gave up!", $"The correct answer was **{currentGame.CorrectAnswer}**");
        response.Components = null;
        response.Embed.WithColor(DiscordConstants.AppleMusicRed);

        if (currentGame.Answers is { Count: >= 1 })
        {
            var separateResponse = new EmbedBuilder();
            separateResponse.WithDescription($"**{userTitle}** gave up! The correct answer was `{currentGame.CorrectAnswer}`");
            separateResponse.WithColor(DiscordConstants.AppleMusicRed);
            if (context.DiscordChannel is IMessageChannel msgChannel)
            {
                _ = Task.Run(() => msgChannel.SendMessageAsync(embed: separateResponse.Build()));
            }
        }

        return response;
    }

    public async Task JumbleProcessAnswer(ContextModel context, ICommandContext commandContext)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.Embed,
        };

        try
        {
            var currentGame = await this._gameService.GetJumbleSessionForChannelId(context.DiscordChannel.Id);
            if (currentGame == null || currentGame.DateEnded.HasValue)
            {
                return;
            }

            var answerIsRight = GameService.AnswerIsRight(currentGame, commandContext.Message.Content);
            var messageLength = commandContext.Message.Content.Length;
            var answerLength = currentGame.CorrectAnswer.Length;

            if (answerIsRight)
            {

                _ = Task.Run(() => commandContext.Message.AddReactionAsync(new Emoji("✅")));

                _ = Task.Run(() => this._gameService.JumbleAddAnswer(currentGame, commandContext.User.Id, true));

                _ = Task.Run(() => this._gameService.JumbleEndSession(currentGame));

                var userTitle = await UserService.GetNameAsync(context.DiscordGuild, context.DiscordUser);

                var separateResponse = new EmbedBuilder();
                separateResponse.WithDescription($"**{userTitle}** got it right! The answer was `{currentGame.CorrectAnswer}`");
                var timeTaken = DateTime.UtcNow - currentGame.DateStarted;
                separateResponse.WithFooter($"Answered in {timeTaken.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s");
                separateResponse.WithColor(DiscordConstants.SpotifyColorGreen);

                if (context.DiscordChannel is IMessageChannel msgChannel)
                {
                    _ = Task.Run(() => msgChannel.SendMessageAsync(embed: separateResponse.Build()));
                }

                if (currentGame.DiscordResponseId.HasValue)
                {
                    BuildJumbleEmbed(response.Embed, currentGame.JumbledArtist, currentGame.Hints, false);
                    response.Components = null;
                    response.Embed.WithColor(DiscordConstants.SpotifyColorGreen);

                    var msg = await commandContext.Channel.GetMessageAsync(currentGame.DiscordResponseId.Value);
                    if (msg is not IUserMessage message)
                    {
                        return;
                    }

                    await message.ModifyAsync(m =>
                    {
                        m.Components = null;
                        m.Embed = response.Embed.Build();
                    });
                }
            }
            else if (messageLength >= answerLength - 5 && messageLength <= answerLength + 2)
            {
                var levenshteinDistance =
                    GameService.GetLevenshteinDistance(currentGame.CorrectAnswer.ToLower(), commandContext.Message.Content.ToLower());

                if (levenshteinDistance == 1)
                {
                    await commandContext.Message.AddReactionAsync(new Emoji("🤏"));
                }
                else
                {
                    await commandContext.Message.AddReactionAsync(new Emoji("❌"));
                }

                await this._gameService.JumbleAddAnswer(currentGame, commandContext.User.Id, false);
            }
        }
        catch (Exception e)
        {
            Log.Error("Error in JumbleProcessAnswer: {exception}", e.Message, e);
        }
    }

    public async Task<ResponseModel> JumbleTimeExpired(ContextModel context, int gameSessionId)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.Embed,
        };

        var currentGame = await this._gameService.GetJumbleSessionForSessionId(gameSessionId);
        if (currentGame == null || currentGame.DateEnded.HasValue)
        {
            return null;
        }

        await this._gameService.JumbleEndSession(currentGame);
        BuildJumbleEmbed(response.Embed, currentGame.JumbledArtist, currentGame.Hints, false);

        response.Embed.AddField("Time is up!", $"The correct answer was **{currentGame.CorrectAnswer}**");
        response.Components = null;
        response.Embed.WithColor(DiscordConstants.AppleMusicRed);

        if (currentGame.Answers is { Count: >= 1 })
        {
            var separateResponse = new EmbedBuilder();
            separateResponse.WithDescription($"Nobody guessed it right. The answer was `{currentGame.CorrectAnswer}`");
            separateResponse.WithColor(DiscordConstants.AppleMusicRed);
            if (context.DiscordChannel is IMessageChannel msgChannel)
            {
                _ = Task.Run(() => msgChannel.SendMessageAsync(embed: separateResponse.Build()));
            }
        }

        return response;
    }

    public async Task<ResponseModel> StartPixelation(ContextModel context, int userId,
    CancellationTokenSource cancellationTokenSource)
    {
        var response = new ResponseModel
        {
            ResponseType = ResponseType.ImageWithEmbed
        };

        //var existingGame = await this._gameService.GetJumbleSessionForChannelId(context.DiscordChannel.Id);
        //if (existingGame != null && !existingGame.DateEnded.HasValue)
        //{
        //    if (existingGame.DateStarted <= DateTime.UtcNow.AddSeconds(-(GameService.SecondsToGuess + 10)))
        //    {
        //        await this._gameService.JumbleEndSession(existingGame);
        //    }
        //    else
        //    {
        //        response.CommandResponse = CommandResponse.Cooldown;
        //        return response;
        //    }
        //}

        //var recentJumbles = await this._gameService.GetJumbleSessionsCountToday(context.ContextUser.UserId);
        //var jumblesPlayedToday = recentJumbles.Count(c => c.DateStarted.Date == DateTime.Today);
        //const int jumbleLimit = 30;
        //if (!SupporterService.IsSupporter(context.ContextUser.UserType) && jumblesPlayedToday > jumbleLimit)
        //{
        //    response.Embed.WithColor(DiscordConstants.InformationColorBlue);
        //    response.Embed.WithDescription($"You've used up all your {jumbleLimit} jumbles of today. [Get supporter]({Constants.GetSupporterDiscordLink}) to play unlimited jumble games and much more.");
        //    response.Components = new ComponentBuilder()
        //        .WithButton(Constants.GetSupporterButton, style: ButtonStyle.Link, url: Constants.GetSupporterDiscordLink);
        //    response.CommandResponse = CommandResponse.SupporterRequired;
        //    return response;
        //}

        var topAlbums = await this._albumService.GetUserAllTimeTopAlbums(userId, true);

        if (topAlbums.Count(c => c.UserPlaycount > 50) <= 5)
        {
            response.Embed.WithColor(DiscordConstants.WarningColorOrange);
            response.Embed.WithDescription($"Sorry, you haven't listened to enough albums yet to use this command. Please scrobble more music and try again later.");
            response.CommandResponse = CommandResponse.NoScrobbles;
            return response;
        }

        await this._albumService.FillMissingAlbumCovers(topAlbums);
        var album = GameService.PickAlbumForPixelation(topAlbums, null);

        if (album == null)
        {
            response.Embed.WithDescription($"You've played all jumbles that are available for you today. Come back tomorrow or scrobble more music to play again.");
            response.CommandResponse = CommandResponse.NotFound;
            return response;
        }

        var databaseAlbum = await this._albumService.GetAlbumFromDatabase(album.ArtistName, album.AlbumName);
        if (databaseAlbum == null)
        {
            // Pick someone else and hope for the best
            album = GameService.PickAlbumForPixelation(topAlbums, null);
            databaseAlbum = await this._albumService.GetAlbumFromDatabase(album.ArtistName, album.AlbumName);
        }


        var game = await this._gameService.StartJumbleGame(userId, context, JumbleType.JumbleFirstWins, album.AlbumName, cancellationTokenSource);

        var databaseArtist = await this._artistsService.GetArtistFromDatabase(album.ArtistName);
        CountryInfo artistCountry = null;
        if (databaseArtist?.CountryCode != null)
        {
            artistCountry = this._countryService.GetValidCountry(databaseArtist.CountryCode);
        }

        var hints = this._gameService.GetJumbleAlbumHints(databaseAlbum, databaseArtist, album.UserPlaycount.GetValueOrDefault(), artistCountry);
        await this._gameService.JumbleStoreShowedHints(game, hints);

        BuildJumbleEmbed(response.Embed, game.JumbledArtist, game.Hints, jumbleType: JumbleType.Pixelation);

        var image = await this._gameService.GetSkImage(album.AlbumCoverUrl, album.AlbumName, album.ArtistName);
        if (image == null)
        {
            response.Embed.WithDescription("Sorry, something went wrong while getting album cover for your album.");
            response.CommandResponse = CommandResponse.Error;
            response.ResponseType = ResponseType.Embed;
            return response;
        }

        var pixelPercentage = 0.1f;
        image = GameService.BlurCoverImage(image, pixelPercentage);

        var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        response.Stream = encoded.AsStream();
        response.FileName = $"pixelation-{game.JumbleSessionId}-{pixelPercentage}.png";

        response.Components = BuildJumbleComponents(game.JumbleSessionId, game.Hints);
        response.GameSessionId = game.JumbleSessionId;

        return response;
    }
}
