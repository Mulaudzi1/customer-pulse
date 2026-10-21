FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/CustomerPulse/CustomerPulse.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Pulse="Data Source=/data/pulse.db"
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "CustomerPulse.dll"]
