using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    public Transform target;
    
    [Header("Position Offset")]
    public Vector3 offset = new Vector3(0f, 6f, -10f);
    public float followSpeed = 8f;

    [Header("Rotation Offset")]
    public float lookAtOffset = 1.5f;
    public float rotationSpeed = 5f;

    void LateUpdate()
    {
        if (target == null) return;

        // Position follow (behind target using its rotation)
        Vector3 targetCamPos = target.position + target.rotation * offset;
        transform.position = Vector3.Lerp(transform.position, targetCamPos, followSpeed * Time.deltaTime);

        // Look at target smoothly
        Vector3 lookAtTarget = target.position + Vector3.up * lookAtOffset;
        Quaternion targetRot = Quaternion.LookRotation(lookAtTarget - transform.position);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);
    }
}
