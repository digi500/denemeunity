using UnityEngine;
using TMPro;

public class DriftScoreSystem : MonoBehaviour
{
    public Rigidbody vehicleRb;
    
    [Header("UI Reference")]
    public TextMeshProUGUI scoreText;
    
    [Header("Scoring Parameters")]
    public float minDriftAngle = 15f;
    public float minSpeed = 5f;
    public float scoreMultiplier = 10f;
    
    private float totalScore = 0f;
    private float currentDriftScore = 0f;
    private bool isDrifting = false;

    // 360 Spin tracking
    private float lastAngle = 0f;
    private float currentSpinRotation = 0f;
    private float spinBonus = 500f;

    void Start()
    {
        if (vehicleRb != null)
        {
            lastAngle = vehicleRb.transform.eulerAngles.y;
        }
        UpdateUI();
    }

    void Update()
    {
        if (vehicleRb == null) return;

        // Calculate drift angle (angle between velocity direction and forward direction)
        float speed = vehicleRb.linearVelocity.magnitude;
        float angle = 0f;

        if (speed > minSpeed)
        {
            angle = Vector3.Angle(vehicleRb.transform.forward, vehicleRb.linearVelocity);
        }

        // Check if we are drifting
        isDrifting = (angle > minDriftAngle && speed > minSpeed);

        if (isDrifting)
        {
            // Accumulate score based on drift angle and speed
            currentDriftScore += angle * speed * scoreMultiplier * Time.deltaTime;

            // Track 360 spin rotation
            float currentAngle = vehicleRb.transform.eulerAngles.y;
            float deltaAngle = Mathf.DeltaAngle(lastAngle, currentAngle);
            currentSpinRotation += Mathf.Abs(deltaAngle);

            if (currentSpinRotation >= 360f)
            {
                currentDriftScore += spinBonus;
                currentSpinRotation -= 360f;
                Debug.Log("360 Drift Bonus!");
            }
        }
        else
        {
            // If we stop drifting, add accumulated drift points to total score
            if (currentDriftScore > 0f)
            {
                totalScore += Mathf.Round(currentDriftScore);
                currentDriftScore = 0f;
            }
            currentSpinRotation = 0f; // Reset spin tracker if drift ends
        }

        if (vehicleRb != null)
        {
            lastAngle = vehicleRb.transform.eulerAngles.y;
        }

        UpdateUI();
    }

    void UpdateUI()
    {
        if (scoreText == null) return;

        if (isDrifting)
        {
            scoreText.text = string.Format(
                "SCORE: {0}\n<color=#FF5500><size=120%>DRIFTING: +{1}</size></color>",
                Mathf.Round(totalScore),
                Mathf.Round(currentDriftScore)
            );
        }
        else
        {
            scoreText.text = string.Format("SCORE: {0}", Mathf.Round(totalScore));
        }
    }
}
