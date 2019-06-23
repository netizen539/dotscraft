using UnityEngine;

public class BasicCamera : MonoBehaviour
{
    public bool locked = true;
    public float speed = 10f;

    float lookAnglesx;
    float lookAnglesy;

    void Start()
    {
    }

    void Update()
    {
        DoMouse();
        DoMovement();
    }

    void DoMouse()
    {
        bool finalLocked = (Input.GetKey(KeyCode.Tab) ? false : locked);
        Cursor.lockState = (finalLocked ? CursorLockMode.Locked : CursorLockMode.None);
        Cursor.visible = !finalLocked;

        lookAnglesx += Input.GetAxis("Mouse X");
        lookAnglesy += Input.GetAxis("Mouse Y");
        lookAnglesy = Mathf.Clamp(lookAnglesy, -89, 89);

        transform.eulerAngles = new Vector3(-lookAnglesy, lookAnglesx, 0);
    }

    void DoMovement()
    {
        Vector3 velocity = new Vector3(Input.GetAxis("Horizontal") * speed, 0, Input.GetAxis("Vertical") * speed);
        transform.Translate(velocity * Time.deltaTime);
    }
}
