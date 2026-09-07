// EasyTier 群友 IP 分配服务
// 部署于腾讯云：为持有效邀请码的群友客户端原子分配虚拟 IP，
// 并下发组网参数（网络名/口令经 HTTPS 返回，口令不落客户端安装包）。
//
// 安全约定：
//   - 所有凭据（网络口令、管理密钥）仅从环境变量读取，邀请码仅从数据目录 codes.json 读取；
//     源码、示例文件中不出现任何可用凭据。
//   - 本服务不主动发起任何对外 HTTP 请求。
//   - 日志脱敏：不打印口令/管理密钥，邀请码与虚拟 IP 仅输出掩码形式。
package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"log"
	"math/big"
	"net"
	"net/http"
	"net/netip"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// errCodeInUse 表示邀请码已绑定在另一台机器上（一码一机）。
var errCodeInUse = errors.New("code_in_use")

// errMachineRequired 表示请求缺少机器标识（旧版客户端）。
var errMachineRequired = errors.New("machine_id_required")

// ---------------------------------------------------------------------------
// 配置
// ---------------------------------------------------------------------------

type config struct {
	listenAddr     string
	networkName    string
	networkSecret  string
	peerURLs       []string
	poolStart      netip.Addr
	poolEnd        netip.Addr
	adminKey       string
	heartbeatTTL   time.Duration
	dataDir        string
	codesPath      string
	heartbeatEvery time.Duration
}

func loadConfig() (config, error) {
	c := config{
		listenAddr:     envOr("LISTEN_ADDR", ":11020"),
		networkName:    os.Getenv("NETWORK_NAME"),
		networkSecret:  os.Getenv("NETWORK_SECRET"),
		adminKey:       os.Getenv("ADMIN_KEY"),
		dataDir:        envOr("DATA_DIR", "./data"),
		heartbeatTTL:   time.Duration(envIntOr("HEARTBEAT_TTL_SEC", 90)) * time.Second,
		heartbeatEvery: time.Duration(envIntOr("HEARTBEAT_INTERVAL_SEC", 30)) * time.Second,
	}
	if c.networkName == "" || c.networkSecret == "" {
		return c, fmt.Errorf("NETWORK_NAME / NETWORK_SECRET 环境变量必须设置")
	}
	if c.adminKey == "" {
		return c, fmt.Errorf("ADMIN_KEY 环境变量必须设置")
	}
	for _, u := range strings.Split(envOr("PEER_URLS", "tcp://101.43.121.186:11010"), ",") {
		if u = strings.TrimSpace(u); u != "" {
			c.peerURLs = append(c.peerURLs, u)
		}
	}
	var err error
	if c.poolStart, err = netip.ParseAddr(envOr("IP_POOL_START", "10.144.0.100")); err != nil {
		return c, fmt.Errorf("IP_POOL_START 无效: %w", err)
	}
	if c.poolEnd, err = netip.ParseAddr(envOr("IP_POOL_END", "10.144.0.109")); err != nil {
		return c, fmt.Errorf("IP_POOL_END 无效: %w", err)
	}
	c.codesPath = filepath.Join(c.dataDir, "codes.json")
	return c, nil
}

func envOr(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func envIntOr(k string, def int) int {
	if v := os.Getenv(k); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 {
			return n
		}
	}
	return def
}

// ---------------------------------------------------------------------------
// 邀请码与租约
// ---------------------------------------------------------------------------

type codeEntry struct {
	Code    string `json:"code"`
	Note    string `json:"note"`
	Revoked bool   `json:"revoked"`
}

type lease struct {
	Code           string    `json:"-"`
	Note           string    `json:"-"`
	MachineID      string    `json:"-"`
	IP             string    `json:"ip"`
	LastHeartbeat  time.Time `json:"-"`
	AllocatedAt    time.Time `json:"allocated_at"`
	HeartbeatsMiss int       `json:"-"`
}

type allocServer struct {
	cfg   config
	mu    sync.Mutex
	codes map[string]codeEntry // code -> entry
	pool  []string             // 全量可用 IP（有序）
	held  map[string]*lease    // ip -> lease
	byCode map[string]string   // code -> ip
}

func newAllocServer(cfg config) (*allocServer, error) {
	s := &allocServer{
		cfg:    cfg,
		codes:  map[string]codeEntry{},
		held:   map[string]*lease{},
		byCode: map[string]string{},
	}
	for a := cfg.poolStart; a.Compare(cfg.poolEnd) <= 0; a = a.Next() {
		s.pool = append(s.pool, a.String())
	}
	if len(s.pool) == 0 {
		return nil, fmt.Errorf("IP 池为空: %s ~ %s", cfg.poolStart, cfg.poolEnd)
	}
	if err := s.loadCodes(); err != nil {
		return nil, err
	}
	return s, nil
}

func (s *allocServer) loadCodes() error {
	raw, err := os.ReadFile(s.cfg.codesPath)
	if err != nil {
		return fmt.Errorf("读取邀请码文件失败(%s): %w", s.cfg.codesPath, err)
	}
	var list []codeEntry
	if err := json.Unmarshal(raw, &list); err != nil {
		return fmt.Errorf("邀请码文件 JSON 无效: %w", err)
	}
	codes := map[string]codeEntry{}
	for _, e := range list {
		e.Code = strings.TrimSpace(e.Code)
		if e.Code == "" {
			continue
		}
		codes[e.Code] = e
	}
	if len(codes) == 0 {
		return fmt.Errorf("邀请码文件为空: %s", s.cfg.codesPath)
	}
	s.mu.Lock()
	s.codes = codes
	s.mu.Unlock()
	log.Printf("已加载邀请码 %d 个", len(codes))
	return nil
}

// alloc 原子分配，一码一机：
//   - 同码同机：幂等返回已持有 IP（重连场景）；
//   - 同码异机：拒绝（errCodeInUse）；
//   - 新码：分配首个空闲 IP 并绑定机器。
func (s *allocServer) alloc(code, machineID string) (string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if ip, ok := s.byCode[code]; ok {
		if l, live := s.held[ip]; live {
			if l.MachineID == machineID {
				l.LastHeartbeat = time.Now()
				return ip, nil
			}
			return "", errCodeInUse
		}
		delete(s.byCode, code) // 脏索引兜底
	}
	for _, ip := range s.pool {
		if _, used := s.held[ip]; !used {
			now := time.Now()
			s.held[ip] = &lease{Code: code, Note: s.codes[code].Note, MachineID: machineID, IP: ip, LastHeartbeat: now, AllocatedAt: now}
			s.byCode[code] = ip
			return ip, nil
		}
	}
	return "", errors.New("pool_exhausted")
}

func (s *allocServer) heartbeat(code, ip, machineID string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	l, ok := s.held[ip]
	if !ok || l.Code != code || l.MachineID != machineID {
		return false
	}
	l.LastHeartbeat = time.Now()
	return true
}

func (s *allocServer) release(code, ip, machineID string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	l, ok := s.held[ip]
	if !ok || l.Code != code || l.MachineID != machineID {
		return false
	}
	delete(s.held, ip)
	delete(s.byCode, code)
	return true
}

// janitor 回收心跳超时的租约。
func (s *allocServer) janitor() {
	t := time.NewTicker(10 * time.Second)
	defer t.Stop()
	for range t.C {
		s.mu.Lock()
		now := time.Now()
		for ip, l := range s.held {
			if now.Sub(l.LastHeartbeat) > s.cfg.heartbeatTTL {
				delete(s.held, ip)
				delete(s.byCode, l.Code)
				log.Printf("租约超时释放: code=%s ip=%s", maskCode(l.Code), maskIP(ip))
			}
		}
		s.mu.Unlock()
	}
}

func (s *allocServer) codeValid(code string) bool {
	e, ok := s.codes[code]
	return ok && !e.Revoked
}

// ---------------------------------------------------------------------------
// HTTP 接口
// ---------------------------------------------------------------------------

type allocReq struct {
	InviteCode string `json:"invite_code"`
	IP         string `json:"ip"`
	MachineID  string `json:"machine_id"`
}

type allocResp struct {
	IP                  string   `json:"ip"`
	NetworkName         string   `json:"network_name"`
	NetworkSecret       string   `json:"network_secret"`
	PeerURLs            []string `json:"peer_urls"`
	HeartbeatIntervalSec int     `json:"heartbeat_interval_sec"`
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, code int, err string) {
	writeJSON(w, code, map[string]string{"error": err})
}

func (s *allocServer) handleAlloc(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	req, ok := decodeBody(w, r)
	if !ok {
		return
	}
	s.mu.Lock()
	valid := s.codeValid(req.InviteCode)
	s.mu.Unlock()
	if !valid {
		log.Printf("alloc 拒绝: code=%s", maskCode(req.InviteCode))
		writeErr(w, http.StatusUnauthorized, "invalid_invite_code")
		return
	}
	if strings.TrimSpace(req.MachineID) == "" {
		writeErr(w, http.StatusBadRequest, "machine_id_required")
		return
	}
	ip, err := s.alloc(req.InviteCode, req.MachineID)
	if errors.Is(err, errCodeInUse) {
		log.Printf("alloc 一码多机拒绝: code=%s", maskCode(req.InviteCode))
		writeErr(w, http.StatusConflict, "code_in_use")
		return
	}
	if err != nil {
		log.Printf("alloc 池满: code=%s", maskCode(req.InviteCode))
		writeErr(w, http.StatusServiceUnavailable, "pool_exhausted")
		return
	}
	log.Printf("alloc 成功: code=%s ip=%s machine=%s", maskCode(req.InviteCode), maskIP(ip), maskMachine(req.MachineID))
	writeJSON(w, http.StatusOK, allocResp{
		IP:                  ip,
		NetworkName:         s.cfg.networkName,
		NetworkSecret:       s.cfg.networkSecret,
		PeerURLs:            s.cfg.peerURLs,
		HeartbeatIntervalSec: int(s.cfg.heartbeatEvery.Seconds()),
	})
}

func (s *allocServer) handleHeartbeat(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	req, ok := decodeBody(w, r)
	if !ok {
		return
	}
	if s.heartbeat(req.InviteCode, req.IP, req.MachineID) {
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
		return
	}
	log.Printf("heartbeat 失配: code=%s ip=%s", maskCode(req.InviteCode), maskIP(req.IP))
	writeErr(w, http.StatusConflict, "lease_lost")
}

func (s *allocServer) handleRelease(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	req, ok := decodeBody(w, r)
	if !ok {
		return
	}
	if s.release(req.InviteCode, req.IP, req.MachineID) {
		log.Printf("release 成功: code=%s ip=%s", maskCode(req.InviteCode), maskIP(req.IP))
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (s *allocServer) handleStatus(w http.ResponseWriter, r *http.Request) {
	key := r.Header.Get("X-Admin-Key")
	if key == "" {
		key = r.URL.Query().Get("admin_key")
	}
	if key != s.cfg.adminKey {
		writeErr(w, http.StatusUnauthorized, "bad_admin_key")
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	type ipRow struct {
		IP       string `json:"ip"`
		Used     bool   `json:"used"`
		Code     string `json:"code,omitempty"`
		Note     string `json:"note,omitempty"`
		Machine  string `json:"machine,omitempty"`
		AgeSec   int64  `json:"age_sec,omitempty"`
	}
	ips := make([]ipRow, 0, len(s.pool))
	for _, ip := range s.pool {
		row := ipRow{IP: maskIP(ip)}
		if l, ok := s.held[ip]; ok {
			row.Used = true
			row.Code = maskCode(l.Code)
			row.Note = l.Note
			row.Machine = maskMachine(l.MachineID)
			row.AgeSec = int64(time.Since(l.AllocatedAt).Seconds())
		}
		ips = append(ips, row)
	}
	codes := make([]codeEntry, 0, len(s.codes))
	for _, e := range s.codes {
		codes = append(codes, codeEntry{Code: maskCode(e.Code), Note: e.Note, Revoked: e.Revoked})
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"pool_total":     len(s.pool),
		"pool_used":      len(s.held),
		"ips":            ips,
		"codes":          codes,
		"heartbeat_ttl":  int(s.cfg.heartbeatTTL.Seconds()),
	})
}

func (s *allocServer) handleReload(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	key := r.Header.Get("X-Admin-Key")
	if key == "" {
		key = r.URL.Query().Get("admin_key")
	}
	if key != s.cfg.adminKey {
		writeErr(w, http.StatusUnauthorized, "bad_admin_key")
		return
	}
	if err := s.loadCodes(); err != nil {
		writeErr(w, http.StatusInternalServerError, "reload_failed")
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func decodeBody(w http.ResponseWriter, r *http.Request) (allocReq, bool) {
	r.Body = http.MaxBytesReader(w, r.Body, 8192)
	var req allocReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_json")
		return req, false
	}
	return req, true
}

// ---------------------------------------------------------------------------
// 自签 TLS 证书（持久化，指纹供客户端内置校验）
// ---------------------------------------------------------------------------

func ensureCert(dir string) (tls.Certificate, string, error) {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return tls.Certificate{}, "", err
	}
	certPath := filepath.Join(dir, "server.pem")
	keyPath := filepath.Join(dir, "server.key")
	if cert, err := tls.LoadX509KeyPair(certPath, keyPath); err == nil {
		return cert, certFingerprint(cert.Certificate[0]), nil
	}

	priv, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, "", err
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return tls.Certificate{}, "", err
	}
	tmpl := x509.Certificate{
		SerialNumber: serial,
		Subject:      pkix.Name{CommonName: "easytier-ip-alloc"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().AddDate(10, 0, 0),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		DNSNames:     []string{"localhost"},
		IPAddresses:  []net.IP{net.ParseIP("127.0.0.1")},
	}
	// 把服务器出口 IP 加进 SAN，便于运维直连调试。
	if conn, err := net.Dial("udp", "8.8.8.8:53"); err == nil {
		if local, ok := conn.LocalAddr().(*net.UDPAddr); ok && local.IP != nil && !local.IP.IsLoopback() {
			tmpl.IPAddresses = append(tmpl.IPAddresses, local.IP)
		}
		_ = conn.Close()
	}
	der, err := x509.CreateCertificate(rand.Reader, &tmpl, &tmpl, &priv.PublicKey, priv)
	if err != nil {
		return tls.Certificate{}, "", err
	}
	keyDER, err := x509.MarshalECPrivateKey(priv)
	if err != nil {
		return tls.Certificate{}, "", err
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})
	if err := os.WriteFile(certPath, certPEM, 0o600); err != nil {
		return tls.Certificate{}, "", err
	}
	if err := os.WriteFile(keyPath, keyPEM, 0o600); err != nil {
		return tls.Certificate{}, "", err
	}
	cert, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		return tls.Certificate{}, "", err
	}
	log.Printf("已生成自签证书: %s", certPath)
	return cert, certFingerprint(der), nil
}

func certFingerprint(der []byte) string {
	sum := sha256.Sum256(der)
	return strings.ToUpper(hex.EncodeToString(sum[:]))
}

// ---------------------------------------------------------------------------
// 脱敏工具
// ---------------------------------------------------------------------------

func maskCode(code string) string {
	if len(code) <= 4 {
		return "****"
	}
	return code[:4] + "****"
}

func maskMachine(id string) string {
	if len(id) <= 8 {
		return "****"
	}
	return id[:8] + "****"
}

func maskIP(ip string) string {
	if i := strings.LastIndex(ip, "."); i > 0 {
		return ip[:i] + ".*"
	}
	return "*.*"
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

func main() {
	log.SetFlags(log.LstdFlags)
	cfg, err := loadConfig()
	if err != nil {
		log.Fatalf("配置错误: %v", err)
	}
	srv, err := newAllocServer(cfg)
	if err != nil {
		log.Fatalf("初始化失败: %v", err)
	}
	cert, fingerprint, err := ensureCert(cfg.dataDir)
	if err != nil {
		log.Fatalf("证书初始化失败: %v", err)
	}
	log.Printf("证书 SHA-256 指纹: %s", fingerprint)

	mux := http.NewServeMux()
	mux.HandleFunc("/api/alloc", srv.handleAlloc)
	mux.HandleFunc("/api/heartbeat", srv.handleHeartbeat)
	mux.HandleFunc("/api/release", srv.handleRelease)
	mux.HandleFunc("/api/status", srv.handleStatus)
	mux.HandleFunc("/api/admin/reload", srv.handleReload)
	mux.HandleFunc("/api/health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
	})
	mux.HandleFunc("/api/fingerprint", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"sha256": fingerprint})
	})

	go srv.janitor()

	httpSrv := &http.Server{
		Addr:              cfg.listenAddr,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
		TLSConfig: &tls.Config{
			Certificates: []tls.Certificate{cert},
			MinVersion:   tls.VersionTLS12,
		},
	}

	go func() {
		log.Printf("IP 分配服务启动: https://%s (池 %s~%s, 共 %d 个)", cfg.listenAddr, cfg.poolStart, cfg.poolEnd, len(srv.pool))
		if err := httpSrv.ListenAndServeTLS("", ""); err != nil && err != http.ErrServerClosed {
			log.Fatalf("服务异常退出: %v", err)
		}
	}()

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	for received := range sig {
		if received == syscall.SIGHUP {
			if err := srv.loadCodes(); err != nil {
				log.Printf("SIGHUP 重载邀请码失败: %v", err)
			}
			continue
		}
		log.Printf("收到信号 %v，正在退出", received)
		_ = httpSrv.Close()
		return
	}
}
