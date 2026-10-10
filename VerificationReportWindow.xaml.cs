using System.Windows;

namespace TSN_MULTI_API
{
    public partial class VerificationReportWindow : Window
    {
        public VerificationReportWindow(string summary, string details, bool isSuccess)
        {
            InitializeComponent();
            TxtIcon.Text = isSuccess ? "✅" : "⚠️";
            TxtSummary.Text = summary;
            TxtDetails.Text = details;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}