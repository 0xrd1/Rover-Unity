#!/usr/bin/env python3
import socket
import json
import rclpy
from rclpy.node import Node
from rclpy.action import ActionClient
from nav_autonomy_interface.action import Mission

class ROSBridge(Node):
    def __init__(self, rover_action_server='mission', rover_host=None):
        super().__init__('ros_bridge')
        self.action_client = ActionClient(self, Mission, rover_action_server)
        self.tcp_port = 5005
        self.tcp_host = '0.0.0.0'
        self.rover_host = rover_host
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.bind((self.tcp_host, self.tcp_port))
        self.sock.listen(1)
        self.get_logger().info(f"ROSBridge listening on {self.tcp_host}:{self.tcp_port}")


    def run(self):
        while rclpy.ok():
            conn, addr = self.sock.accept()
            with conn:
                self.get_logger().info(f"Connection from {addr}")
                data = b''
                while True:
                    chunk = conn.recv(4096)
                    if not chunk:
                        break
                    data += chunk
                try:
                    mission = json.loads(data.decode('utf-8'))
                    self.get_logger().info(f"Received mission: {mission}")
                    self.send_mission(mission)
                except Exception as e:
                    self.get_logger().error(f"Failed to parse mission: {e}")


    def send_mission(self, mission):
        waypoints = mission.get('nav_waypoints', [])
        if not waypoints or len(waypoints) == 0:
            self.get_logger().warn("Received mission with no waypoints. Not sending to rover.")
            return
        goal_msg = Mission.Goal()
        goal_msg.search_object = mission.get('search_object', 0)
        goal_msg.search_pattern = mission.get('search_pattern', 0)
        goal_msg.nav_waypoints = []
        for wp in waypoints:
            waypoint = Mission.Goal.NavWaypoints()
            waypoint.latitude = wp.get('latitude', 0.0)
            waypoint.longitude = wp.get('longitude', 0.0)
            goal_msg.nav_waypoints.append(waypoint)
        self.get_logger().info(f"Sending mission goal to rover...")
        self.action_client.wait_for_server()
        future = self.action_client.send_goal_async(goal_msg)
        rclpy.spin_until_future_complete(self, future)
        result = future.result()
        self.get_logger().info(f"Mission goal sent. Result: {result}")


def main():
    rclpy.init()
    node = ROSBridge()
    try:
        node.run()
    except KeyboardInterrupt:
        pass
    finally:
        node.destroy_node()
        rclpy.shutdown()

if __name__ == '__main__':
    main()
