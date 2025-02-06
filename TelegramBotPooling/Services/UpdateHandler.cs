using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using TelegramBotPooling.Api.Service;
using TelegramBotPooling.Configs;
using TelegramBotPooling.ExternalApi.GoogleSheets;

namespace TelegramBotPooling.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly ILogger<UpdateHandler> _logger;
    private readonly GoogleSheetConfig _googleSheetConfig;
    private readonly TelegramBotConfig _telegramBotConfig;
    private readonly ITelegramBotClient _botClient;
    private readonly IMessageService _messageService;
    private readonly IBaseParser _baseParser;
    private readonly IWebsiteHeadersHandler _websiteHeadersHandler;


    public UpdateHandler(
        ILogger<UpdateHandler> logger,
        IOptions<GoogleSheetConfig> googleSheetConfig,
        IOptions<TelegramBotConfig> telegramBotConfig,
        ITelegramBotClient botClient,
        IMessageService messageService,
        IBaseParser baseParser,
        IWebsiteHeadersHandler websiteHeadersHandler
    )
    {
        _logger = logger;
        _googleSheetConfig = googleSheetConfig.Value;
        _telegramBotConfig = telegramBotConfig.Value;
        _botClient = botClient;
        _messageService = messageService;
        _baseParser = baseParser;
        _websiteHeadersHandler = websiteHeadersHandler;
    }


    public async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Username {Username} with Id {Id} | sent a message: {Message}",
            update.Message!.From!.Username,
            update.Message.From.Id,
            update.Message.Text
        );

        var handler = update switch
        {
            { Message: { } message }                       => BotOnMessageReceived(message, cancellationToken),
            { EditedMessage: { } message }                 => BotOnMessageReceived(message, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(update), update, null)
        };

        await handler;
    }


    private async Task BotOnMessageReceived(Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Receive message type: {MessageType}", message.Type);
        if (message.Text is not { } messageText)
            return;

        var action = messageText.Split(' ')[0] switch
        {
            "/forcestart"                                      => StartWebsiteHandler(_botClient, message, cancellationToken),
            "/forcestart@notification_ax_link_test_bot"        => StartWebsiteHandler(_botClient, message, cancellationToken),
            _                                                  => throw new ArgumentOutOfRangeException()
        };
        var sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    public  async Task<Message> StartWebsiteHandler(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var accessibleLinksCount = 0;
        var notAccessibleLinksCount = 0;

        var linksGroupByCategoryAndThemCount = new ConcurrentDictionary<string, (int totalLinks, int accessibleLinks)>();
        var linksWithNames = new Dictionary<string, List<string>>();

        var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{_googleSheetConfig!.SheetId}/values/{_googleSheetConfig.Range}?key={_googleSheetConfig.ApiKey}";
        var response = await _baseParser.ParseGet<Sheet1>(apiUrl, cancellationToken);

        var linkCount = response!.Values.Skip(1).Count(row => row.Count > 3 && !string.IsNullOrEmpty(row[3]?.ToString()));
        var calculatedTime = linkCount / 40;

        if (message.Text == "/forcestart")
        {
            var user = message.From;
            var userInfo = $"User info:\n\n" +
                              $"ID: {user.Id}\n" +
                              $"Name: {user.FirstName}\n" +
                              $"Surname: {user.LastName ?? "not set"}\n" +
                              $"Username: @{user.Username ?? "not set"}\n" +
                              $"Language Code: {user.LanguageCode ?? "not set"}\n" +
                              $"Is premium: {(user.IsPremium.HasValue && user.IsPremium.Value ? "YES" : "NO")}\n" +
                              $"Is added to attach menu: {(user.AddedToAttachmentMenu.HasValue && user.AddedToAttachmentMenu.Value ? "YES" : "NO")}\n" +
                              $"Is user a bot: {(user.IsBot ? "YES" : "NO")}";
            
            _logger.LogInformation("Command '/forcestart' received. User details:\n{UserInfo}", userInfo);

            await botClient.SendTextMessageAsync(chatId: message.Chat.Id, text: $"-- Process has been started.\nApproximate time to complete:  ~{calculatedTime} minutes ", cancellationToken: cancellationToken);
        }

        await botClient.SendTextMessageAsync(chatId: _telegramBotConfig.PrivateChatId, text: $"-- Process has been started by command -- \nApproximate time to complete:  ~{calculatedTime} minutes ", cancellationToken: cancellationToken);

        _logger.LogInformation(
            "\n ===== Google Sheet. response.Values.Skip(1).Count: {Count} | Response.Values.Count: {ValueCount} ===== \n",
            response!.Values.Skip(1).Count(),
            response.Values.Count
        );

        if (response?.Values != null && response.Values.Any())
        {
            foreach (var row in response.Values.Skip(1))
            {
                var category = row.FirstOrDefault()?.ToString();
                var name = row.Skip(1).FirstOrDefault()?.ToString();
                var link = row.Skip(3).FirstOrDefault()?.ToString();

                if (string.IsNullOrEmpty(link))
                {
                    continue;
                }

                if (!linksGroupByCategoryAndThemCount.ContainsKey(category!))
                {
                    linksGroupByCategoryAndThemCount[category!] = (0, 0);
                }

                var isAccessible = await _websiteHeadersHandler.HeaderHandlerAsync(link);
                // _logger.LogDebug($"Site {link} is accessible: {isAccessible}");

                if (isAccessible)
                {
                    linksGroupByCategoryAndThemCount[category!] = (linksGroupByCategoryAndThemCount[category!].totalLinks + 1, linksGroupByCategoryAndThemCount[category!].accessibleLinks + 1);
                    accessibleLinksCount ++;
                }
                else
                {
                    await Task.Delay(10_000, cancellationToken);

                    isAccessible = await _websiteHeadersHandler.HeaderHandlerAsync(link);

                    if (isAccessible)
                    {
                        linksGroupByCategoryAndThemCount[category!] = (linksGroupByCategoryAndThemCount[category!].totalLinks + 1, linksGroupByCategoryAndThemCount[category!].accessibleLinks + 1);
                        accessibleLinksCount++;
                    }
                    else
                    {
                        linksGroupByCategoryAndThemCount[category!] = (linksGroupByCategoryAndThemCount[category!].totalLinks + 1, linksGroupByCategoryAndThemCount[category!].accessibleLinks);

                        linksWithNames.TryGetValue(name!, out var linkList);
                        if (linkList == null)
                        {
                            linkList = new List<string>();
                            linksWithNames[name!] = linkList;
                        }

                        linksWithNames[name!].Add(link);
                        notAccessibleLinksCount++;
                    }

                }
            }
        }

        await _messageService.SendInfo(linksGroupByCategoryAndThemCount.ToDictionary(data => data.Key, data => data.Value), accessibleLinksCount, _telegramBotConfig!.PrivateChatId, cancellationToken);
        await _messageService.SendError(linksWithNames.ToDictionary(data => data.Key, data => data.Value.ToList()), notAccessibleLinksCount, _telegramBotConfig.PrivateChatId, cancellationToken);

        return await botClient.SendTextMessageAsync(
            chatId: _telegramBotConfig.PrivateChatId,
            text: "-- Process has been done --",
            cancellationToken: cancellationToken);
    }


    public async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var ErrorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogInformation("HandleError: {ErrorMessage}", ErrorMessage);

        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}