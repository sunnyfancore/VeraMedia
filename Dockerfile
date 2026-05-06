FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

EXPOSE 8080

COPY publish/ .
ENV TZ=Asia/Shanghai
ENV ASPNETCORE_URLS=http://+:8080
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

ENTRYPOINT ["dotnet", "VeraMedia.Api.dll"]
