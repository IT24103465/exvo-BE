FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/services/TicketService/Exvo.TicketService.csproj src/services/TicketService/
RUN dotnet restore src/services/TicketService/Exvo.TicketService.csproj

COPY src/services/TicketService/ src/services/TicketService/
RUN dotnet publish src/services/TicketService/Exvo.TicketService.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Exvo.TicketService.dll"]
