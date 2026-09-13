FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY HomeOps.sln ./
COPY src/HomeOps.Api/HomeOps.Api.csproj src/HomeOps.Api/
RUN dotnet restore src/HomeOps.Api/HomeOps.Api.csproj
COPY src/HomeOps.Api/ src/HomeOps.Api/
RUN dotnet publish src/HomeOps.Api/HomeOps.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "HomeOps.Api.dll"]
