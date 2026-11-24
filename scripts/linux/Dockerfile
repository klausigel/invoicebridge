FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ghostscript=10.02.1~dfsg1-0ubuntu7.10 \
        openjdk-21-jre-headless=21.0.13.1+2-1~24.04.1 python3 \
    && rm -rf /var/lib/apt/lists/*
