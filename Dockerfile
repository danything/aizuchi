# Native AOT なので SDK でビルドして runtime-deps に置くだけ。最終イメージは 30MB 台。
# 実行段は Ubuntu の chiseled (distroless。シェルもパッケージマネージャも無い)。chiseled は glibc なので、
# ビルドも同じ Ubuntu (resolute) の SDK で linux-x64 向けに組む (alpine の SDK だと linux-musl-x64 になり、
# chiseled では動かない)。-aot の SDK には AOT のネイティブリンクに要る clang などが入っている
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute-aot AS build
WORKDIR /work
COPY Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/Aizuchi/Aizuchi.csproj -c Release -o /out

# 繋ぐのは glibc・libstdc++・OpenSSL (TLS) と CA 証明書だけで、どれも chiseled の runtime-deps にある
# (ICU は InvariantGlobalization なので要らない)。利用者は今までどおり app (uid 1654)
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled
WORKDIR /app
COPY --from=build /out/aizuchi .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# 死活の確認は k8s のプローブ (charts/aizuchi の /healthz・/readyz) でする
ENTRYPOINT ["./aizuchi"]
