FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

EXPOSE 8080

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        libreoffice \
        poppler-utils \
        espeak-ng \
        python3 \
        fonts-noto-cjk \
    && rm -rf /var/lib/apt/lists/*

COPY publish/ .
RUN cp /app/scripts/export-ppt-video.sh /usr/local/bin/export-ppt-video \
    && chmod +x /usr/local/bin/export-ppt-video
ENV TZ=Asia/Shanghai
ENV ASPNETCORE_URLS=http://+:8080
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

ENTRYPOINT ["dotnet", "VeraMedia.Api.dll"]
