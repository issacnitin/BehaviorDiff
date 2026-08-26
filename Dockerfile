# syntax=docker/dockerfile:1.7

ARG DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0-noble
ARG RUNTIME_IMAGE=ubuntu:24.04
ARG JAVA_IMAGE=maven:3.9.11-eclipse-temurin-17
ARG NODE_IMAGE=node:24-bookworm-slim
ARG GO_IMAGE=golang:1.27-bookworm
ARG RUST_IMAGE=rust:1.98-bookworm

FROM ${JAVA_IMAGE} AS java-build
WORKDIR /source
COPY src/BehaviorDiff.Java.Agent/pom.xml .
RUN mvn --batch-mode --no-transfer-progress dependency:go-offline
COPY src/BehaviorDiff.Java.Agent/ .
RUN mvn --batch-mode --no-transfer-progress package -DskipTests \
    && mkdir -p /out \
    && find target -maxdepth 1 -type f -name 'behaviordiff-java-agent-*.jar' ! -name 'original-*' \
        -exec cp '{}' /out/behaviordiff-java-agent.jar \;

FROM ${NODE_IMAGE} AS node-build
WORKDIR /out
COPY src/BehaviorDiff.Node/package.json src/BehaviorDiff.Node/package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts --no-audit --no-fund
COPY src/BehaviorDiff.Node/register.cjs src/BehaviorDiff.Node/loader.mjs src/BehaviorDiff.Node/bootstrap.mjs ./
COPY src/BehaviorDiff.Node/src/ ./src/
COPY src/BehaviorDiff.Node/adapters/ ./adapters/
RUN rm -rf node_modules/.bin

FROM ${GO_IMAGE} AS go-build
ARG TARGETARCH
WORKDIR /source
COPY src/BehaviorDiff.Go/go.mod ./
RUN go mod download
COPY src/BehaviorDiff.Go/ .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && mkdir -p "/out/$rid" \
    && CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o "/out/$rid/behaviordiff-go-rewrite" ./cmd/behaviordiff-go-rewrite

FROM ${RUST_IMAGE} AS rust-build
ARG TARGETARCH
WORKDIR /source
COPY src/BehaviorDiff.Engine.Rust/Cargo.toml src/BehaviorDiff.Engine.Rust/Cargo.lock ./
RUN mkdir src && printf 'fn main() {}\n' > src/main.rs && cargo build --release --locked && rm -rf src
COPY src/BehaviorDiff.Engine.Rust/src/ ./src/
RUN touch src/main.rs \
    && cargo build --release --locked \
    && case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && mkdir -p "/out/$rid" \
    && cp target/release/behaviordiff-engine "/out/$rid/behaviordiff-engine"

FROM ${RUST_IMAGE} AS rust-tracer-build
ARG TARGETARCH
WORKDIR /source
COPY src/BehaviorDiff.Rust.Tracer/Cargo.toml src/BehaviorDiff.Rust.Tracer/Cargo.lock ./
COPY src/BehaviorDiff.Rust.Tracer/runtime/ ./runtime/
COPY src/BehaviorDiff.Rust.Tracer/src/ ./src/
RUN cargo build --release --locked \
    && case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && mkdir -p "/out/$rid" \
    && cp target/release/behaviordiff-rust-rewrite "/out/$rid/behaviordiff-rust-rewrite"

FROM ${DOTNET_IMAGE} AS dotnet-build
ARG TARGETARCH
WORKDIR /source
COPY global.json Directory.Build.props README.md ./
COPY src/BehaviorDiff.Contracts/ src/BehaviorDiff.Contracts/
COPY src/BehaviorDiff.Tracer/ src/BehaviorDiff.Tracer/
COPY src/BehaviorDiff.Tracer.Xunit/ src/BehaviorDiff.Tracer.Xunit/
COPY src/BehaviorDiff.Cli/ src/BehaviorDiff.Cli/
COPY tools/Weaver/ tools/Weaver/
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet publish src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj \
        --configuration Release --runtime "$rid" --self-contained true --output /out --nologo \
        --source https://www.nuget.org/api/v2/ \
        -p:NuGetAudit=false -p:SelfContainedRelease=true -p:PublishTrimmed=false

FROM ${RUNTIME_IMAGE} AS runtime
ARG BUILD_VERSION=dev
LABEL org.opencontainers.image.title="BehaviorDiff" \
    org.opencontainers.image.description="BehaviorDiff CLI, engine, and .NET, Java, Node, Go, and Rust tracing toolchains" \
      org.opencontainers.image.source="https://github.com/issacnitin/BehaviorDiff" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get install --yes --no-install-recommends build-essential ca-certificates git libicu74 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet-build /usr/share/dotnet /usr/share/dotnet
COPY --from=java-build /opt/java/openjdk /opt/java/openjdk
COPY --from=java-build /usr/share/maven /usr/share/maven
COPY --from=node-build /usr/local/bin/node /usr/local/bin/node
COPY --from=node-build /usr/local/lib/node_modules /usr/local/lib/node_modules
COPY --from=go-build /usr/local/go /usr/local/go
COPY --from=rust-tracer-build /usr/local/cargo /usr/local/cargo
COPY --from=rust-tracer-build /usr/local/rustup /usr/local/rustup

RUN ln -s /usr/share/maven/bin/mvn /usr/local/bin/mvn \
    && ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && ln -s /opt/behaviordiff/tracers/go/linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')/behaviordiff-go-rewrite \
        /usr/local/bin/behaviordiff-go-rewrite

COPY --from=dotnet-build /out/ /opt/behaviordiff/
COPY --from=java-build /out/behaviordiff-java-agent.jar /opt/behaviordiff/tracers/java/behaviordiff-java-agent.jar
COPY --from=node-build /out/ /opt/behaviordiff/tracers/node/
COPY --from=go-build /out/ /opt/behaviordiff/tracers/go/
COPY --from=rust-build /out/ /opt/behaviordiff/engines/rust/
COPY --from=rust-tracer-build /out/ /opt/behaviordiff/tracers/rust/
COPY docker/behaviordiff.sh /usr/local/bin/behaviordiff
COPY docker/action-entrypoint.sh /usr/local/bin/behaviordiff-action
COPY docker/entrypoint.sh /usr/local/bin/behaviordiff-entrypoint

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    JAVA_HOME=/opt/java/openjdk \
    CARGO_HOME=/usr/local/cargo \
    RUSTUP_HOME=/usr/local/rustup \
    BEHAVIORDIFF_JAVA_AGENT=/opt/behaviordiff/tracers/java/behaviordiff-java-agent.jar \
    BEHAVIORDIFF_NODE_TRACER=/opt/behaviordiff/tracers/node \
    DOTNET_ROOT=/usr/share/dotnet \
    PATH=/opt/java/openjdk/bin:/usr/local/go/bin:/usr/local/cargo/bin:/usr/share/dotnet:${PATH}

RUN chmod +x /usr/local/bin/behaviordiff /usr/local/bin/behaviordiff-action \
    /usr/local/bin/behaviordiff-entrypoint \
    /opt/behaviordiff/tracers/go/*/behaviordiff-go-rewrite \
    /opt/behaviordiff/engines/rust/*/behaviordiff-engine \
    /opt/behaviordiff/tracers/rust/*/behaviordiff-rust-rewrite \
    && dotnet --info >/dev/null \
    && java -version \
    && mvn --version \
    && node --version \
    && npm --version \
    && go version \
    && cargo --version \
    && rustc --version

WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/behaviordiff-entrypoint"]
