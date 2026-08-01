"""Local dev server for the InventorRayOptics web app, with caching disabled so
browser refreshes always pick up the latest code. Always serves this script's own
directory regardless of the caller's working directory."""
import http.server
import os
import sys


class NoCacheHandler(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header('Cache-Control', 'no-cache')
        super().end_headers()


if __name__ == '__main__':
    os.chdir(os.path.dirname(os.path.abspath(__file__)))
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8360
    http.server.ThreadingHTTPServer(('', port), NoCacheHandler).serve_forever()
