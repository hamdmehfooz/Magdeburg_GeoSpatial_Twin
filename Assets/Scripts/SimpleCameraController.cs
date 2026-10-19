using UnityEngine;

public class SimpleFreeCamera : MonoBehaviour
{
    public float moveSpeed = 20f;
    public float mouseSensitivity = 200f;

    float rotX;
    float rotY;

    void Start()
    {
        Cursor.lockState = CursorLockMode.None;
    }

    void Update()
    {
        // 🔥 Mouse Look (hold right click)
        if (Input.GetMouseButton(1))
        {
            rotX += Input.GetAxis("Mouse X") * mouseSensitivity * Time.deltaTime;
            rotY -= Input.GetAxis("Mouse Y") * mouseSensitivity * Time.deltaTime;

            rotY = Mathf.Clamp(rotY, -80f, 80f);

            transform.rotation = Quaternion.Euler(rotY, rotX, 0);
        }

        // 🔥 Movement
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        Vector3 move = transform.forward * v + transform.right * h;

        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

        transform.position += move * moveSpeed * Time.deltaTime;
    }
}