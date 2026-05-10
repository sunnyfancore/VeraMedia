FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime
WORKDIR /app

EXPOSE 8080

# System packages + Intel VAAPI runtime for GPU video encoding
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    rm -f /etc/apt/apt.conf.d/docker-clean \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        vainfo \
        intel-media-va-driver \
        libmfx1 \
        libreoffice-impress-nogui \
        default-jre-headless \
        poppler-utils \
        mupdf-tools \
        fonts-noto-cjk \
        curl \
        ca-certificates

# Pre-warm font cache and LibreOffice profile
RUN fc-cache -fv \
    && soffice --headless --version

COPY publish/ .
ENV TZ=Asia/Shanghai
ENV ASPNETCORE_URLS=http://+:8080
ENV SAL_NO_XINITTHREADS=1
ENV HOME=/tmp
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

ENTRYPOINT ["dotnet", "VeraMedia.Api.dll"]
