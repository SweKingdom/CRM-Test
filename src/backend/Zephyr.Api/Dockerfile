FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Zephyr.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish Zephyr.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Zephyr.Api.dll"]
