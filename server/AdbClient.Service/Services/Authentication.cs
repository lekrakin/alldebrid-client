using AdbClient.Data.Data;
using Microsoft.AspNetCore.Identity;

namespace AdbClient.Service.Services;

public class Authentication(SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager, UserData userData)
{
    private static readonly SemaphoreSlim RegistrationLock = new(1, 1);

    public async Task<IdentityResult> Register(string userName, string password)
    {
        await RegistrationLock.WaitAsync();

        try
        {
            if (await GetUser() != null)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "AccountAlreadyExists",
                    Description = "An account already exists."
                });
            }

            return await userManager.CreateAsync(new IdentityUser(userName), password);
        }
        finally
        {
            RegistrationLock.Release();
        }
    }

    public async Task<SignInResult> Login(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return SignInResult.Failed;
        }

        var result = await signInManager.PasswordSignInAsync(userName, password, true, false);

        return result;
    }

    public async Task<IdentityUser?> GetUser()
    {
        return await userData.GetUser();
    }

    public async Task Logout()
    {
        await signInManager.SignOutAsync();
    }

    public async Task<IdentityResult> Update(string? newUserName, string? newPassword)
    {
        var user = await GetUser();

        if (user == null)
        {
            if (string.IsNullOrWhiteSpace(newUserName) || string.IsNullOrWhiteSpace(newPassword))
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "InitialCredentialsRequired",
                    Description = "Enter both a username and password to create the first account."
                });
            }

            return await Register(newUserName, newPassword);
        }

        if (!string.IsNullOrWhiteSpace(newUserName))
        {
            user.UserName = newUserName;
            var updateResult = await userManager.UpdateAsync(user);

            if (!updateResult.Succeeded)
            {
                return updateResult;
            }
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return IdentityResult.Success;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        return await userManager.ResetPasswordAsync(user, token, newPassword);
    }
}
