FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/NotificationService.Worker/NotificationService.Worker.csproj src/NotificationService.Worker/
RUN dotnet restore src/NotificationService.Worker/NotificationService.Worker.csproj
COPY src/Contracts/ src/Contracts/
COPY src/NotificationService.Worker/ src/NotificationService.Worker/
RUN dotnet publish src/NotificationService.Worker/NotificationService.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NotificationService.Worker.dll"]
