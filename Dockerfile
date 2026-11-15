FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY API/API.csproj API/
COPY LINEAPI/LINEAPI.csproj LINEAPI/
RUN dotnet restore API/API.csproj
COPY API/ API/
COPY LINEAPI/ LINEAPI/
RUN dotnet publish API/API.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
COPY --from=build /out/ .
USER $APP_UID
ENTRYPOINT ["dotnet", "API.dll"]
