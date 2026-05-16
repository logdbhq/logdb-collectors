FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app
COPY --from=build /app .

ENV COLLECTOR_API_URL=http://logdb-nginx-collector:8080
ENV ASPNETCORE_URLS=http://+:8081

EXPOSE 8081

ENTRYPOINT ["dotnet", "com.logdb.nginx.collector.ui.dll"]
