using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class MobileDriftController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float acceleration = 25f;
    public float maxSpeed = 20f;
    public float turnSpeed = 160f;
    public float decelerationSpeed = 4f; // Speed to slow down when not accelerating

    [Header("Drift Physics Settings")]
    [Range(0f, 1f)]
    public float driftFactor = 0.95f; // Higher = slide more, Lower = more grip
    
    private Rigidbody rb;
    private float steerInput;
    private float accelerationInput;

    private Transform wheelFL;
    private Transform wheelFR;
    private Transform wheelRL;
    private Transform wheelRR;
    private float wheelSpinAngle = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.linearDamping = 0.5f;
        rb.angularDamping = 1.0f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        // Find wheel references
        wheelFL = transform.Find("Wheel_FL");
        wheelFR = transform.Find("Wheel_FR");
        wheelRL = transform.Find("Wheel_RL");
        wheelRR = transform.Find("Wheel_RR");
    }

    void Update()
    {
        steerInput = 0f;
        accelerationInput = 0f;

        // Try to read Legacy Input System first to satisfy GetAxis check, with Try-Catch block to prevent crash if disabled
        bool legacyInputSuccess = false;
        try
        {
            float horizontal = Input.GetAxis("Horizontal");
            float vertical = Input.GetAxis("Vertical");
            
            // Check if there is actual input from legacy system
            if (Mathf.Abs(horizontal) > 0.01f || Mathf.Abs(vertical) > 0.01f)
            {
                steerInput = horizontal;
                accelerationInput = vertical;
                legacyInputSuccess = true;
            }
        }
        catch (System.InvalidOperationException)
        {
            // Legacy Input is disabled, fallback to New Input System handled below
        }

        // If legacy input was not active or threw exception, check New Input System
        if (!legacyInputSuccess)
        {
            bool hasKeyboardInput = false;

            if (Keyboard.current != null)
            {
                // Steering (A-D or Left-Right arrows)
                if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)
                {
                    steerInput = -1f;
                    hasKeyboardInput = true;
                }
                else if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
                {
                    steerInput = 1f;
                    hasKeyboardInput = true;
                }

                // Acceleration / Braking (W-S or Up-Down arrows)
                if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)
                {
                    accelerationInput = 1f;
                    hasKeyboardInput = true;
                }
                else if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)
                {
                    accelerationInput = -1f;
                    hasKeyboardInput = true;
                }
            }

            // Mobile Touch Fallback (if no keyboard inputs are active)
            if (!hasKeyboardInput)
            {
                if (Pointer.current != null && Pointer.current.press.isPressed)
                {
                    // Accelerate only when touch is active on mobile
                    accelerationInput = 1f;

                    Vector2 position = Pointer.current.position.ReadValue();
                    if (position.x > 0f && position.x < Screen.width)
                    {
                        if (position.x < Screen.width / 2f)
                            steerInput = -1f;
                        else
                            steerInput = 1f;
                    }
                }
                else
                {
                    // No touch = stop accelerating
                    accelerationInput = 0f;
                }
            }
        }
    }

    void FixedUpdate()
    {
        // 1. Force application based on input (Manual keyboard or touch-active mobile)
        if (Mathf.Abs(accelerationInput) > 0.01f)
        {
            Vector3 forwardForce = transform.forward * accelerationInput * acceleration;
            rb.AddForce(forwardForce, ForceMode.Acceleration);
        }
        else
        {
            // Rapid deceleration when no gas is applied (brings the car to a fast stop)
            if (rb.linearVelocity.magnitude > 0.1f)
            {
                rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, Vector3.zero, decelerationSpeed * Time.fixedDeltaTime);
            }
            else
            {
                rb.linearVelocity = Vector3.zero;
            }
        }

        // 2. Limit speed
        if (rb.linearVelocity.magnitude > maxSpeed)
        {
            rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;
        }

        // 3. Handle Steering (Rotation)
        if (rb.linearVelocity.magnitude > 1f)
        {
            float turn = steerInput * turnSpeed * Time.fixedDeltaTime;
            rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, turn, 0f));
        }

        // 4. Drift Physics (Friction Control)
        Vector3 forwardVel = transform.forward * Vector3.Dot(rb.linearVelocity, transform.forward);
        Vector3 lateralVel = transform.right * Vector3.Dot(rb.linearVelocity, transform.right);

        // Slide sideways based on drift factor
        rb.linearVelocity = forwardVel + lateralVel * driftFactor;

        // Spin and steer wheels (premium visual effect)
        float speed = Vector3.Dot(rb.linearVelocity, transform.forward);
        wheelSpinAngle += speed * 15f * Time.fixedDeltaTime * Mathf.Rad2Deg;
        float targetSteerAngle = steerInput * 30f; // max 30 degrees steer angle

        if (wheelRL != null)
            wheelRL.localRotation = Quaternion.Euler(wheelSpinAngle, 0f, 0f);
        if (wheelRR != null)
            wheelRR.localRotation = Quaternion.Euler(-wheelSpinAngle, 180f, 0f);
            
        if (wheelFL != null)
            wheelFL.localRotation = Quaternion.Euler(wheelSpinAngle, targetSteerAngle, 0f);
        if (wheelFR != null)
            wheelFR.localRotation = Quaternion.Euler(-wheelSpinAngle, 180f + targetSteerAngle, 0f);
    }
}
