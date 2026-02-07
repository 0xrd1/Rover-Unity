#!/usr/bin/env python3
import socket
import json

HOST = '127.0.0.1' # Use localhost for local testing
PORT = 5005

def start_server():
    # SO_REUSEADDR allows us to restart the script immediately
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            s.bind((HOST, PORT))
            print(f"Successfully bound to {HOST}:{PORT}")
        except Exception as e:
            print(f"Failed to bind: {e}")
        s.listen(1)
        s.settimeout(1.0) # Check for Ctrl+C every 1 second
        
        print(f"Server live at {HOST}:{PORT}. Press Ctrl+C to stop.")

        while True:
            try:
                try:
                    conn, addr = s.accept()
                except socket.timeout:
                    continue # Loop back and check Ctrl+C again
                
                with conn:
                    print(f"\n[CONNECTED] {addr}")
                    data_str = ""
                    while True:
                        chunk = conn.recv(4096).decode('utf-8')
                        if not chunk: break
                        data_str += chunk
                        # Break if we see the newline Unity sends
                        if "\n" in data_str: break 
                    
                    try:
                        # Pretty print the JSON we just got
                        mission_data = json.loads(data_str.strip())
                        print("[RECEIVED MISSION]:")
                        print(json.dumps(mission_data, indent=4))
                    except json.JSONDecodeError:
                        print(f"[ERROR] Received non-JSON data: {data_str}")
                        
            except KeyboardInterrupt:
                print("\n[STOPPING] Server shutting down.")
                break

if __name__ == "__main__":
    start_server()