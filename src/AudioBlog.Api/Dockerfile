# Build from the repository root:  docker build -f src/AudioBlog.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/AudioBlog.Core/AudioBlog.Core.csproj src/AudioBlog.Core/
COPY src/AudioBlog.Api/AudioBlog.Api.csproj src/AudioBlog.Api/
RUN dotnet restore src/AudioBlog.Api/AudioBlog.Api.csproj
COPY src/AudioBlog.Core/ src/AudioBlog.Core/
COPY src/AudioBlog.Api/ src/AudioBlog.Api/
RUN dotnet publish src/AudioBlog.Api/AudioBlog.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AudioBlog.Api.dll"]
