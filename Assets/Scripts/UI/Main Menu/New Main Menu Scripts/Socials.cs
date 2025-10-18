using UnityEngine;

public class SocialLinks : MonoBehaviour
{
    // Opens your Reddit profile
    public void OpenReddit()
    {
        Application.OpenURL("https://www.reddit.com/user/JustTwoGuysGames/");
    }

    // Opens your Discord invite
    public void OpenDiscord()
    {
        Application.OpenURL("https://discord.gg/848Kk6kvDf");
    }

    // Opens your Twitter (X) profile
    public void OpenTwitter()
    {
        Application.OpenURL("https://x.com/JsTwoGuysStudio");
    }

     public void OpenWebsite()
    {
        Application.OpenURL("https://www.justtwoguys.com");
    }
}
