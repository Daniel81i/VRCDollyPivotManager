"""oscquery.py

OSCQuery の最小実装。

VRChat との間で2つのことをする。

1. **自分を公開する** — mDNS で `_oscjson._tcp` と `_osc._udp` を広告し、
   HTTP でノードツリーを返す。VRChat はこれを見つけて、こちらが指定した
   UDP ポートへ OSC を送ってくる。ポートを両側で合わせる必要がなくなる。

2. **VRChat に問い合わせる** — VRChat 自身も OSCQuery エンドポイントを
   公開している。mDNS で見つけて HTTP GET すると、**全パラメータの現在値**が
   まとめて取れる。素の OSC は値が変化したときにしか送られてこないため、
   起動直後に現在値を知る手段がこれしかない。

依存は zeroconf のみ。HTTP は標準ライブラリで足りる。
"""

from __future__ import annotations

import json
import socket
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Callable, Dict, List, Optional, Tuple

try:
    from zeroconf import ServiceBrowser, ServiceInfo, ServiceListener, Zeroconf
    AVAILABLE = True
except ImportError:  # zeroconf が無ければ OSCQuery は使えない
    AVAILABLE = False
    Zeroconf = None  # type: ignore

OSCJSON_TYPE = "_oscjson._tcp.local."
OSC_TYPE = "_osc._udp.local."

# mDNS で名乗る名前。exe をリネームしても変わらないよう固定する。
SERVICE_NAME = "VRCDollyPivotManager"

# VRChat 自身が名乗る名前の接頭辞。同じネットワークに別の OSCQuery
# サービスが居ることがあるため、問い合わせ先はこれを優先する。
VRCHAT_PREFIX = "VRChat"

PARAM_ROOT = "/avatar/parameters/"


def free_port() -> int:
    """空いている TCP ポートを1つ確保して返す。"""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def local_ip() -> str:
    """mDNS で広告するアドレス。常にループバック。

    VRChat は仕様として localhost からの通信しか受け付けない。LAN の IP を
    広告すると、見つけてもらえても VRChat 側から送ってこない。以前は
    外向きのソケットから LAN の IP を取って広告していたため、OSCQuery 経由の
    受信が成立していなかった。VRChat 公式のライブラリも既定は localhost。
    """
    return "127.0.0.1"


# ---------------------------------------------------------------------------
# 自分を公開する側
# ---------------------------------------------------------------------------

class _Handler(BaseHTTPRequestHandler):
    """OSCQuery が要求する最小限の応答だけ返す。"""

    tree: Dict[str, Any] = {}
    host_info: Dict[str, Any] = {}

    def do_GET(self) -> None:  # noqa: N802  (BaseHTTPRequestHandler の規約)
        body = self.host_info if "HOST_INFO" in self.path else self.tree
        payload = json.dumps(body).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_args: Any) -> None:
        """標準エラーへの出力を抑止する。コンソールを持たないため。"""


class Service:
    """自分を OSCQuery サービスとして公開する。"""

    def __init__(self, name: str, osc_port: int) -> None:
        self.name = name
        self.osc_port = osc_port
        self.http_port: Optional[int] = None
        self._zeroconf: Optional[Any] = None
        self._server: Optional[ThreadingHTTPServer] = None
        self._infos: List[Any] = []

    def start(self, paths: List[str]) -> Tuple[bool, str]:
        if not AVAILABLE:
            return False, "zeroconf が無いため OSCQuery を使えません"

        try:
            self.http_port = free_port()
            _Handler.host_info = {
                "NAME": self.name,
                "OSC_IP": "127.0.0.1",
                "OSC_PORT": self.osc_port,
                "OSC_TRANSPORT": "UDP",
                "EXTENSIONS": {"ACCESS": True, "VALUE": True, "DESCRIPTION": True},
            }
            _Handler.tree = self._build_tree(paths)

            self._server = ThreadingHTTPServer(("0.0.0.0", self.http_port), _Handler)
            threading.Thread(target=self._server.serve_forever, daemon=True).start()

            address = socket.inet_aton(local_ip())
            self._zeroconf = Zeroconf()
            for service_type, port in ((OSCJSON_TYPE, self.http_port),
                                       (OSC_TYPE, self.osc_port)):
                info = ServiceInfo(
                    service_type,
                    f"{self.name}.{service_type}",
                    addresses=[address],
                    port=port,
                    properties={},
                    server=f"{self.name}.local.",
                )
                self._zeroconf.register_service(info)
                self._infos.append(info)

            return True, (f"OSCQuery で公開しました "
                          f"(HTTP {self.http_port} / OSC 受信 {self.osc_port})")
        except Exception as exc:
            self.stop()
            return False, f"OSCQuery の公開に失敗しました: {exc}"

    @staticmethod
    def _build_tree(paths: List[str]) -> Dict[str, Any]:
        """受け取りたいアドレスをノードツリーとして表現する。

        VRChat はこのツリーを読んで、該当するパラメータを送ってくる。
        """
        contents: Dict[str, Any] = {}
        for path in paths:
            node = contents
            parts = [p for p in path.strip("/").split("/") if p]
            for index, part in enumerate(parts):
                leaf = index == len(parts) - 1
                node.setdefault(part, {"FULL_PATH": "/" + "/".join(parts[:index + 1]),
                                       "ACCESS": 1 if leaf else 0})
                if not leaf:
                    node = node[part].setdefault("CONTENTS", {})
        return {"FULL_PATH": "/", "ACCESS": 0, "CONTENTS": contents}

    def stop(self) -> None:
        for info in self._infos:
            try:
                self._zeroconf.unregister_service(info)
            except Exception:
                pass
        self._infos.clear()
        if self._zeroconf is not None:
            try:
                self._zeroconf.close()
            except Exception:
                pass
            self._zeroconf = None
        if self._server is not None:
            try:
                self._server.shutdown()
                self._server.server_close()
            except Exception:
                pass
            self._server = None


# ---------------------------------------------------------------------------
# VRChat に問い合わせる側
# ---------------------------------------------------------------------------

class _Collector(ServiceListener if AVAILABLE else object):  # type: ignore[misc]
    def __init__(self) -> None:
        self.found: List[Tuple[str, int, str]] = []

    def add_service(self, zc: Any, type_: str, name: str) -> None:
        try:
            info = zc.get_service_info(type_, name, timeout=2000)
        except Exception:
            return
        if info is None or not info.addresses:
            return
        host = socket.inet_ntoa(info.addresses[0])
        self.found.append((host, info.port, name))

    def update_service(self, *_args: Any) -> None:
        pass

    def remove_service(self, *_args: Any) -> None:
        pass


def discover(timeout: float = 3.0) -> List[Tuple[str, int, str]]:
    """OSCQuery を公開しているサービスを列挙する。"""
    if not AVAILABLE:
        return []

    zeroconf = Zeroconf()
    collector = _Collector()
    try:
        ServiceBrowser(zeroconf, OSCJSON_TYPE, collector)
        threading.Event().wait(timeout)
    finally:
        try:
            zeroconf.close()
        except Exception:
            pass
    return collector.found


def _walk(node: Dict[str, Any], out: Dict[str, Any]) -> None:
    path = node.get("FULL_PATH")
    value = node.get("VALUE")
    if path and isinstance(value, list) and value:
        out[path] = value[0]
    for child in (node.get("CONTENTS") or {}).values():
        _walk(child, out)


def fetch_parameters(host: str, port: int, timeout: float = 3.0) -> Dict[str, Any]:
    """指定した OSCQuery エンドポイントから /avatar 以下の現在値を取る。

    /avatar/parameters だけでは足りない。目線の高さ（/avatar/eyeheight）は
    parameters の外にあり、座標の正規化に必要なため /avatar から取る。
    """
    url = f"http://{host}:{port}/avatar"
    with urllib.request.urlopen(url, timeout=timeout) as response:
        # BOM を付けて返す実装があるため utf-8-sig で読む。
        # 素の utf-8 だと先頭の BOM で json.loads が落ちる。
        tree = json.loads(response.read().decode("utf-8-sig"))

    values: Dict[str, Any] = {}
    _walk(tree, values)
    return values


def fetch_from_any(timeout: float = 3.0,
                   log: Optional[Callable[[str], None]] = None,
                   skip_name: Optional[str] = None) -> Dict[str, Any]:
    """見つかった OSCQuery サービスを順に試し、最初に取れた現在値を返す。

    skip_name には自分が広告している名前を渡す。こちらも OSCQuery サービス
    として名乗っているので、指定しないと自分自身に問い合わせにいく。
    """
    services = discover(timeout)
    if not services:
        if log:
            log("OSCQuery サービスが見つかりませんでした（VRChat 側の OSC 有効化を確認）")
        return {}

    # VRChat を先に試す。別のサービスが先に値を返すと、そちらを
    # VRChat の現在値として取り込んでしまう。
    services.sort(key=lambda s: not s[2].startswith(VRCHAT_PREFIX))

    for host, port, name in services:
        # 自分の広告。問い合わせても現在値は返らないので飛ばす
        if skip_name and name.split(".")[0] == skip_name:
            continue

        try:
            values = fetch_parameters(host, port, timeout)
        except Exception as exc:
            if log:
                log(f"  {name} から取得できませんでした: {exc}")
            continue
        if values:
            if log:
                log(f"  {name} から {len(values)} 件の現在値を取得しました")
            return values

    return {}
