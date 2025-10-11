#!/bin/bash
source /opt/ros/humble/setup.bash
cd ~/Rover-Unity/ros2_ws
. install/setup.bash
ros2 launch py_pubsub local_net_launch.py
