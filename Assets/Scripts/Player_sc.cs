using UnityEngine;

public class Player_sc : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField]
    private float speed = 5.0f;
    public float horizontalInput;
    void Start()
    {
        transform.position = new Vector3(-2,0,0);
    }
    // Update is called once per frame
    void Update()
    {
        Calculate_Movement();
    }
    void Calculate_Movement()
    {
        // Klavyeden yatay ve dikey inputları al
        float horizontalInput = Input.GetAxis("Horizontal");
        float verticalInput = Input.GetAxis("Vertical");

        // Hareket yönünü belirle
        Vector3 direction = new Vector3(horizontalInput, verticalInput, 0);

        // Nesneyi hareket ettir
        transform.Translate(direction * speed * Time.deltaTime);

        // X eksenini de clamp kullanarak sınırlandıralım
        transform.position = new Vector3(
            Mathf.Clamp(transform.position.x, -11.3f, 11.3f), // X ekseni için sınır
            Mathf.Clamp(transform.position.y, -3.8f, 0),      // Y ekseni için sınır
            0 // Z ekseni sabit tutuluyor
        );

    }
}
