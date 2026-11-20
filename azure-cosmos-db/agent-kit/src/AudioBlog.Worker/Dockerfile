# Build from the repository root:  docker build -f src/AudioBlog.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/AudioBlog.Core/AudioBlog.Core.csproj src/AudioBlog.Core/
COPY src/AudioBlog.Worker/AudioBlog.Worker.csproj src/AudioBlog.Worker/
RUN dotnet restore src/AudioBlog.Worker/AudioBlog.Worker.csproj
COPY src/AudioBlog.Core/ src/AudioBlog.Core/
COPY src/AudioBlog.Worker/ src/AudioBlog.Worker/
RUN dotnet publish src/AudioBlog.Worker/AudioBlog.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
WORKDIR /app
COPY --from=build /app .

USER $APP_UID
ENTRYPOINT ["dotnet", "AudioBlog.Worker.dll"]
