# Native AOT なので SDK でビルドして runtime-deps に置くだけ。最終イメージは 10MB 台。
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
# AOT のネイティブリンクに要るツールチェーン
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /work
COPY Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/Aizuchi/Aizuchi.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine
WORKDIR /app
COPY --from=build /out/aizuchi .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["./aizuchi"]
