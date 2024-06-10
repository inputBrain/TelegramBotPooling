using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly IConfiguration _configuration;
    private readonly ITelegramBotClient _botClient;
    private readonly IMessageService _messageService;
    private readonly IBaseParser _baseParser;
    private readonly IWebsiteHeadersHandler _websiteHeadersHandler;


    public UpdateHandler(ILogger<UpdateHandler> logger, IConfiguration configuration, ITelegramBotClient botClient, IMessageService messageService, IBaseParser baseParser, IWebsiteHeadersHandler websiteHeadersHandler)
    {
        _logger = logger;
        _configuration = configuration;
        _botClient = botClient;
        _messageService = messageService;
        _baseParser = baseParser;
        _websiteHeadersHandler = websiteHeadersHandler;
    }


    public async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken cancellationToken)
    {
        var botConfig = _configuration.GetSection("TelegramBot").Get<TelegramBotConfig>()!;
        var handler = update switch
        {
            { Message: { } message }                       => BotOnMessageReceived(botConfig, message, cancellationToken),
            { EditedMessage: { } message }                 => BotOnMessageReceived(botConfig, message, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(update), update, null)
        };

        await handler;
    }


    private async Task BotOnMessageReceived(TelegramBotConfig botConfig, Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Receive message type: {MessageType}", message.Type);
        if (message.Text is not { } messageText)
            return;

        var action = messageText.Split(' ')[0] switch
        {
            "/forcestart"                           => StartWebsiteHandler(_botClient, botConfig, message, cancellationToken),
            "/forcestart@AxLinkKeyboard_bot"        => StartWebsiteHandler(_botClient, botConfig, message, cancellationToken),
            _                                       => throw new ArgumentOutOfRangeException()
        };
        var sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    public  async Task<Message> StartWebsiteHandler(ITelegramBotClient botClient, TelegramBotConfig botConfig, Message message, CancellationToken cancellationToken)
    {
        var googleSheetConfig = _configuration.GetSection("GoogleSheet").Get<GoogleSheetConfig>();

        var accessibleLinksCount = 0;
        var notAccessibleLinksCount = 0;

        var linksGroupByCategoryAndThemCount = new ConcurrentDictionary<string, (int totalLinks, int accessibleLinks)>();
        var linksWithNames = new ConcurrentDictionary<string, ConcurrentBag<string>>();

        var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{googleSheetConfig!.SheetId}/values/{googleSheetConfig.Range}?key={googleSheetConfig.ApiKey}";
        var response = await _baseParser.ParseGet<Sheet1>(apiUrl, cancellationToken);

        var linkCount = response!.Values.Skip(1).Count(row => row.Count > 3 && !string.IsNullOrEmpty(row[3]?.ToString()));
        var calculatedTime = linkCount / 40;

        await botClient.SendTextMessageAsync(chatId: botConfig.PrivateChatId, text: $"-- Process has been started by command -- \nApproximate time to complete:  ~{calculatedTime} minutes ", cancellationToken: cancellationToken);

        _logger.LogInformation(
            "\n ===== Google Sheet. response.Values.Skip(1).Count: {Count} | Response.Values.Count: {ValueCount} ===== \n",
            response!.Values.Skip(1).Count(),
            response.Values.Count
        );

        if (response?.Values != null && response.Values.Any())
        {
            var batchedRows = BatchHelper.Batch(response.Values.Skip(1), 10);

            foreach (var batch in batchedRows)
            {
                var tasks = batch.Select(
                    async row => {
                        var category = row.FirstOrDefault()?.ToString();
                        var name = row.Skip(1).FirstOrDefault()?.ToString();
                        var link = row.Skip(3).FirstOrDefault()?.ToString();

                        if (string.IsNullOrEmpty(link))
                        {
                            return;
                        }

                        linksGroupByCategoryAndThemCount.AddOrUpdate(category!, (1, 0), (key, value) => (value.totalLinks + 1, value.accessibleLinks));

                        var isAccessible = await _websiteHeadersHandler.HeaderHandlerAsync(link);
                        _logger.LogInformation($"Site {link} is accessible: {isAccessible}");

                        if (isAccessible)
                        {
                            linksGroupByCategoryAndThemCount.AddOrUpdate(category!, (1, 1), (key, value) => (value.totalLinks, value.accessibleLinks + 1));
                            Interlocked.Increment(ref accessibleLinksCount);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                            isAccessible = await _websiteHeadersHandler.HeaderHandlerAsync(link);
                            _logger.LogInformation($"\t\tAfter waiting for 10 seconds, one more request was sent. Is accessible: {isAccessible}");

                            if (isAccessible)
                            {
                                linksGroupByCategoryAndThemCount.AddOrUpdate(category!, (1, 1), (key, value) => (value.totalLinks, value.accessibleLinks + 1));
                                Interlocked.Increment(ref accessibleLinksCount);
                            }
                            else
                            {
                                linksGroupByCategoryAndThemCount.AddOrUpdate(category!, (1, 0), (key, value) => (value.totalLinks, value.accessibleLinks));

                                linksWithNames.AddOrUpdate(name!, new ConcurrentBag<string> { link }, (key, list) =>
                                {
                                    list.Add(link);
                                    return list;
                                });

                                Interlocked.Increment(ref notAccessibleLinksCount);
                            }
                        }
                    }).ToList();

                await Task.WhenAll(tasks);
            }
        }

        await _messageService.SendInfo(linksGroupByCategoryAndThemCount.ToDictionary(data => data.Key, data => data.Value), accessibleLinksCount, botConfig!.PrivateChatId, cancellationToken);
        await _messageService.SendError(linksWithNames.ToDictionary(data => data.Key, data => data.Value.ToList()), notAccessibleLinksCount, botConfig.PrivateChatId, cancellationToken);

        return await botClient.SendTextMessageAsync(
            chatId: botConfig.PrivateChatId,
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