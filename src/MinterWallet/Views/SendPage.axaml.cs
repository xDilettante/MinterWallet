using Avalonia.Controls;

namespace MinterWallet.Views;

public partial class SendPage : UserControl
{
    public SendPage()
    {
        InitializeComponent();
        // A contact picked in the list becomes the recipient and the list closes.
        ContactsList.SelectionChanged += (_, _) =>
        {
            if (ContactsList.SelectedItem is not ContactItem contact || DataContext is not MainViewModel main) return;
            main.Send.PickContact.Execute(contact);
            ContactsList.SelectedItem = null;
            ContactsButton.Flyout?.Hide();
        };
        // A contact picked for a multisend becomes a row.
        MultisendContacts.SelectionChanged += (_, _) =>
        {
            if (MultisendContacts.SelectedItem is not ContactItem contact || DataContext is not MainViewModel main) return;
            main.Multisend.PickContact.Execute(contact);
            MultisendContacts.SelectedItem = null;
        };
    }
}
