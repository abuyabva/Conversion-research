FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ConversionAgent/ConversionAgent.csproj src/ConversionAgent/
RUN dotnet restore src/ConversionAgent/ConversionAgent.csproj
COPY src/ConversionAgent/ src/ConversionAgent/
RUN dotnet publish src/ConversionAgent/ConversionAgent.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ConversionAgent.dll"]
