# 多阶段构建：编译期用 golang，运行期用 alpine（含 wget 供健康检查）
FROM golang:1.23-alpine AS build
WORKDIR /src
COPY go.mod main.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/ip-alloc-service .

FROM alpine:3.20
RUN adduser -D -H -u 1000 app
USER app
WORKDIR /app
COPY --from=build /out/ip-alloc-service /app/ip-alloc-service
EXPOSE 11020
VOLUME /data
ENTRYPOINT ["/app/ip-alloc-service"]
