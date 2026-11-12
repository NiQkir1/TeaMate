FROM alpine:3.21 AS whisper-build
RUN apk add --no-cache git cmake make g++ linux-headers
RUN git clone --depth 1 --branch v1.7.6 https://github.com/ggml-org/whisper.cpp.git /whisper \
    && cmake -S /whisper -B /whisper/build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DGGML_OPENMP=OFF -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=ON -DCMAKE_EXE_LINKER_FLAGS="-static" \
    && cmake --build /whisper/build --target whisper-cli -j"$(nproc)" \
    && cp /whisper/build/bin/whisper-cli /usr/local/bin/whisper-cli

FROM mwader/static-ffmpeg:7.1 AS ffmpeg-bin

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY src/Fotoprint.Core/Fotoprint.Core.csproj src/Fotoprint.Core/
COPY src/Fotoprint.AiGateway.Api/Fotoprint.AiGateway.Api.csproj src/Fotoprint.AiGateway.Api/
RUN --mount=type=cache,id=fotoprint-nuget,target=/root/.nuget/packages dotnet restore src/Fotoprint.AiGateway.Api/Fotoprint.AiGateway.Api.csproj

COPY src/Fotoprint.Core/ src/Fotoprint.Core/
COPY src/Fotoprint.AiGateway.Api/ src/Fotoprint.AiGateway.Api/
RUN --mount=type=cache,id=fotoprint-nuget,target=/root/.nuget/packages dotnet publish src/Fotoprint.AiGateway.Api/Fotoprint.AiGateway.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=ffmpeg-bin /ffmpeg /usr/bin/ffmpeg
COPY --from=whisper-build /usr/local/bin/whisper-cli /usr/local/bin/whisper-cli
COPY --from=build /out .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fotoprint.AiGateway.Api.dll"]

