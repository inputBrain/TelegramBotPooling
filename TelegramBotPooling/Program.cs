using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using TelegramBotPooling.Configs;
using TelegramBotPooling.Services;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(
        config =>
        {
            config.AddJsonFile("appsettings.json")
                .Build();
        })
    .ConfigureServices(
        (hostContext, services) =>
        {

            var telegramConfig = hostContext.Configuration.GetSection("TelegramBot").Get<TelegramBotConfig>();
            if (telegramConfig == null)
            {
                throw new Exception("\n\n -----ERROR ATTENTION! ----- \n Telegram bot config 'TelegramBot' is null or does not exist. \n\n");
            }

            var botClient = new TelegramBotClient(telegramConfig.BotToken);

            services.AddSingleton<ITelegramBotClient>(botClient);

            services.AddScoped<UpdateHandler>();
            services.AddScoped<ReceiverService>();
            services.AddHostedService<PollingService>();

        })
    .Build();

host.Run();