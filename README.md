# PuckGtaxBot

## Prerequisites

- .NET 10

## Running the Bot

1. Add Discord bot token to appsettings.json
1. `dotnet run`

## Adding Bot to Server

1. Invite bot to your server with this [link](https://discord.com/oauth2/authorize?client_id=1437560035344384110&scope=bot%20applications.commands&permissions=8)

1. Use `/setup {channel}` command to indicate where NeatQueue spits out the match embed message. It is important the bot has permissions in this channel. 

1. Use `/generatetax` to see a goalie tax schedule based on the last queue's players to confirm you set things up right!

1. Message Jabba if you have any issues