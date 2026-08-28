FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY YnabMcp.Server/YnabMcp.Server.csproj YnabMcp.Server/
RUN dotnet restore YnabMcp.Server/YnabMcp.Server.csproj

COPY YnabMcp.Server/ YnabMcp.Server/
RUN dotnet publish YnabMcp.Server/YnabMcp.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "YnabMcp.Server.dll"]
