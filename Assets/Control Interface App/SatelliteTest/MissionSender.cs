using UnityEngine;
using System.Net.Sockets;
using System.Text;

public class MissionSender : MonoBehaviour
{
    [Header("TCP Settings")]
    public string rosHost = "127.0.0.1";
    public int rosPort = 5005;

    [Header("Mission Source")]
    public SatelliteMapSystem mapSystem;

    [ContextMenu("Send Mission to ROS")]
    public void SendMission()
    {
        Debug.Log($"Sending Mission");
        if (mapSystem == null)
        {
            Debug.LogError("MissionSender: mapSystem not assigned.");
            return;
        }
        string missionJson = mapSystem.GetMissionJson();
        if (string.IsNullOrEmpty(missionJson) || missionJson == "{}")
        {
            Debug.LogError("MissionSender: No mission to send.");
            return;
        }
        // Debug.Log($"Sending Mission JSON: {missionJson}");
        try
        {
            using (TcpClient client = new TcpClient(rosHost, rosPort))
            {
                client.SendTimeout = 5000;
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] data = Encoding.UTF8.GetBytes(missionJson + "\n");
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                    Debug.Log("Mission sent to ROS node.");
                }
            }
        }
        catch (SocketException ex)
        {
            Debug.LogError($"MissionSender: Socket error: {ex.Message}");
        }
    }
}
